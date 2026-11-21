using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    private static readonly string[] CombinationalExprFields = ["value", "condition", "count"];

    // Each step owns complete words. The DAG settles once in dependency order;
    // a procedural block is never retriggered by its own blocking writes.
    private IReadOnlyList<JsonElement> SettledSteps { get; set; } = [];
    internal IReadOnlyList<IReadOnlyList<JsonElement>> SettledLevels { get; private set; } = [];

    private List<JsonElement> SortCombinational(JsonElement root, HashSet<string> sequential, Dictionary<string, JsonElement> continuous)
    {
        var steps = new List<JsonElement>();
        foreach (var assignment in continuous.Values)
            steps.Add(Element("{\"kind\":\"blocking\",\"target\":"+assignment.GetProperty("target").GetRawText()+",\"value\":"+assignment.GetProperty("value").GetRawText()+"}"));
        if (root.TryGetProperty("combinational", out var blocks))
        {
            if(blocks.ValueKind!=JsonValueKind.Array || blocks.GetArrayLength()>Limits.MaxContinuous) throw new IrValidationException($"Combinational block count must be 0..{Limits.MaxContinuous}.");
            foreach(var block in blocks.EnumerateArray())
            {
                HashSet<string> owned = [with(StringComparer.Ordinal)];
                ValidateStatement(block,owned,2); _=StatementCost(block);
                HashSet<string> assigned = [with(StringComparer.Ordinal)];
                ValidateCombFlow(block,owned,assigned);
                if(!owned.SetEquals(assigned)) throw new IrValidationException("Combinational destinations require assignment on every path.");
                steps.Add(block.Clone());
            }
        }
        foreach(var step in steps.Where(e=>Kind(e)=="blocking")){var owned=TargetLeaves(step.GetProperty("target")).Select(TargetOwner).ToHashSet(StringComparer.Ordinal);ValidateCombFlow(step,owned,[with(StringComparer.Ordinal)]);}
        if(steps.Sum(StatementCost)>Limits.MaxNodes)throw new IrValidationException("Expanded combinational statement budget exceeded.");
        Dictionary<string,int> owners = [with(StringComparer.Ordinal)];
        var writes=steps.Select(step=>Statements(step).Where(e=>Kind(e)=="blocking").SelectMany(e=>TargetLeaves(e.GetProperty("target"))).Select(TargetOwner).ToHashSet(StringComparer.Ordinal)).ToArray();
        for(int i=0;i<steps.Count;i++) foreach(var word in writes[i])
            if(sequential.Contains(word)||!owners.TryAdd(word,i))throw new IrValidationException("Conflicting combinational writer.");
        var reads=steps.Select(step=>Statements(step).SelectMany(e=>CombinationalExprFields.Where(n=>e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Object).SelectMany(n=>References(e.GetProperty(n)))).ToHashSet(StringComparer.Ordinal)).ToArray();
        foreach(var read in reads)if(read.Intersect(EventSignals.Append(Clock)).Any())throw new IrValidationException("Clock-dependent combinational logic is outside the race-free subset.");
        var pending=Enumerable.Range(0,steps.Count).ToHashSet();var ordered=new List<JsonElement>();var levels=new List<IReadOnlyList<JsonElement>>();
        while(pending.Count>0)
        {
            var ready=pending.Where(i=>!reads[i].Any(n=>owners.TryGetValue(n,out int owner)&&owner!=i&&pending.Contains(owner))).ToArray();
            if(ready.Length==0)throw new IrValidationException("Combinational feedback is unsupported.");
            var levelSteps = new List<JsonElement>(ready.Length);
            foreach(int i in ready){ordered.Add(steps[i]);levelSteps.Add(steps[i]);pending.Remove(i);}
            levels.Add(levelSteps);
        }
        SettledSteps=ordered;
        SettledLevels=levels;
        // Preserve the continuous-only public inventory; execution uses the unified graph.
        return ordered.Where(e=>Kind(e)=="blocking"&&Kind(e.GetProperty("target"))=="ref"&&continuous.ContainsKey(Name(e.GetProperty("target")))).Select(e=>continuous[Name(e.GetProperty("target"))]).ToList();
    }

    private void ValidateCombFlow(JsonElement statement, HashSet<string> owned, HashSet<string> assigned)
    {
        void Read(JsonElement expression)
        {
            if(References(expression).Any(n=>owned.Contains(n)&&!assigned.Contains(n)))throw new IrValidationException("Combinational storage read before definite assignment.");
        }
        switch(Kind(statement))
        {
            case "block": foreach(var item in statement.GetProperty("items").EnumerateArray())ValidateCombFlow(item,owned,assigned); break;
            case "blocking":
                var target=statement.GetProperty("target");
                var leaves=TargetLeaves(target).ToArray();
                if(leaves.Any(leaf=>Kind(leaf)!="ref"))throw new IrValidationException("Combinational writes currently require whole words.");
                // A concat snapshots its entire RHS before publishing any of its distinct parents.
                Read(statement.GetProperty("value"));assigned.UnionWith(leaves.Select(Name));break;
            case "if":
                Read(statement.GetProperty("condition"));
                var yes=new HashSet<string>(assigned,StringComparer.Ordinal);var no=new HashSet<string>(assigned,StringComparer.Ordinal);
                ValidateCombFlow(statement.GetProperty("then"),owned,yes);ValidateCombFlow(statement.GetProperty("else"),owned,no);
                yes.IntersectWith(no);assigned.UnionWith(yes);break;
            case "case":
                Read(statement.GetProperty("value"));var paths=new List<HashSet<string>>();bool hasDefault=false;
                foreach(var item in statement.GetProperty("items").EnumerateArray()){
                    hasDefault|=IsCaseDefault(item);
                    var path=new HashSet<string>(assigned,StringComparer.Ordinal);ValidateCombFlow(item.GetProperty("body"),owned,path);paths.Add(path);
                }
                if(!hasDefault)paths.Add(new(assigned,StringComparer.Ordinal));
                if(paths.Count>0){var common=paths[0];foreach(var path in paths.Skip(1))common.IntersectWith(path);assigned.UnionWith(common);}break;
            case "while": case "do": case "runtime_repeat":
                if(statement.TryGetProperty("condition",out var condition))Read(condition);
                if(statement.TryGetProperty("count",out var count))Read(count);
                var loopAssigned=new HashSet<string>(assigned,StringComparer.Ordinal);
                ValidateCombFlow(statement.GetProperty("body"),owned,loopAssigned);
                // A runtime loop may execute zero times; its body cannot establish
                // definite assignment for any subsequent condition or statement.
                if(statement.TryGetProperty("condition",out condition))Read(condition);
                break;
            case "repeat":
                var bodyAssigned=new HashSet<string>(assigned,StringComparer.Ordinal);ValidateCombFlow(statement.GetProperty("body"),owned,bodyAssigned);
                if(statement.GetProperty("count").GetInt32()>0)assigned.UnionWith(bodyAssigned);break;
            default: throw new IrValidationException("Combinational blocks require untimed blocking statements.");
        }
    }

    private static JsonElement Element(string json) { using var document=JsonDocument.Parse(json);return document.RootElement.Clone(); }

    private void EmitSettleStep(StringBuilder code, JsonElement step, List<(string Id, string Kind)>? coverage = null)
    {
        if(Kind(step)=="blocking"&&Kind(step.GetProperty("target"))=="ref")
        {
            int id=SignalId(Name(step.GetProperty("target")));
            var rhs=step.GetProperty("value");
            code.AppendLine($"s[{id}]={StoreWord(Expression(rhs,Signals[id].Width),Signals[id],ExpressionWidth(rhs))};");
        }
        else
        {
            code.AppendLine("{auto next=s;");int unique=0;Statement(code,step,ref unique,coverage:coverage);code.AppendLine("}");
        }
    }

    private void EmitSettle(StringBuilder code, List<(string Id, string Kind)>? coverage = null, int threads = 1)
    {
        if (threads <= 1)
        {
            foreach (var step in SettledSteps) EmitSettleStep(code, step, coverage);
            return;
        }
        foreach (var level in SettledLevels)
        {
            bool allSimpleRef = level.Count >= 2 && level.All(st => Kind(st) == "blocking" && Kind(st.GetProperty("target")) == "ref");
            if (!allSimpleRef)
            {
                foreach (var step in level) EmitSettleStep(code, step, coverage);
                continue;
            }
            code.AppendLine($"_vs_pool.parallel_for({level.Count},[&](size_t _lvl_i){{switch(_lvl_i){{");
            for (int idx = 0; idx < level.Count; ++idx)
            {
                code.Append($"case {idx}:{{");
                EmitSettleStep(code, level[idx], coverage);
                code.AppendLine("}break;");
            }
            code.AppendLine("}});");
        }
    }
}
