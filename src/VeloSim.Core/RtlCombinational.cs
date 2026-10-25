using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    // Each step owns complete words. The DAG settles once in dependency order;
    // a procedural block is never retriggered by its own blocking writes.
    private IReadOnlyList<JsonElement> SettledSteps { get; set; } = [];

    private List<JsonElement> SortCombinational(JsonElement root, HashSet<string> sequential, Dictionary<string, JsonElement> continuous)
    {
        var steps = new List<JsonElement>();
        foreach (var assignment in continuous.Values)
            steps.Add(Element("{\"kind\":\"blocking\",\"target\":"+assignment.GetProperty("target").GetRawText()+",\"value\":"+assignment.GetProperty("value").GetRawText()+"}"));
        if (root.TryGetProperty("combinational", out var blocks))
        {
            if(blocks.ValueKind!=JsonValueKind.Array || blocks.GetArrayLength()>256) throw new IrValidationException("Combinational block count must be 0..256.");
            foreach(var block in blocks.EnumerateArray())
            {
                var owned=new HashSet<string>(StringComparer.Ordinal);
                ValidateStatement(block,owned,2); _=StatementCost(block);
                var assigned=new HashSet<string>(StringComparer.Ordinal);
                ValidateCombFlow(block,owned,assigned);
                if(!owned.SetEquals(assigned)) throw new IrValidationException("Combinational destinations require assignment on every path.");
                steps.Add(block.Clone());
            }
        }
        foreach(var step in steps.Where(e=>Kind(e)=="blocking")){var owned=TargetLeaves(step.GetProperty("target")).Select(TargetOwner).ToHashSet(StringComparer.Ordinal);ValidateCombFlow(step,owned,new(StringComparer.Ordinal));}
        if(steps.Sum(StatementCost)>20000)throw new IrValidationException("Expanded combinational statement budget exceeded.");
        var owners=new Dictionary<string,int>(StringComparer.Ordinal);
        var writes=steps.Select(step=>Statements(step).Where(e=>Kind(e)=="blocking").SelectMany(e=>TargetLeaves(e.GetProperty("target"))).Select(TargetOwner).ToHashSet(StringComparer.Ordinal)).ToArray();
        for(int i=0;i<steps.Count;i++) foreach(var word in writes[i])
            if(sequential.Contains(word)||!owners.TryAdd(word,i))throw new IrValidationException("Conflicting combinational writer.");
        var reads=steps.Select(step=>Statements(step).SelectMany(e=>new[]{"value","condition"}.Where(n=>e.TryGetProperty(n,out _)).SelectMany(n=>References(e.GetProperty(n)))).ToHashSet(StringComparer.Ordinal)).ToArray();
        foreach(var read in reads)if(read.Intersect(EventSignals.Append(Clock)).Any())throw new IrValidationException("Clock-dependent combinational logic is outside the race-free subset.");
        var pending=Enumerable.Range(0,steps.Count).ToHashSet();var ordered=new List<JsonElement>();
        while(pending.Count>0)
        {
            var ready=pending.Where(i=>!reads[i].Any(n=>owners.TryGetValue(n,out int owner)&&owner!=i&&pending.Contains(owner))).ToArray();
            if(ready.Length==0)throw new IrValidationException("Combinational feedback is unsupported.");
            foreach(int i in ready){ordered.Add(steps[i]);pending.Remove(i);}
        }
        SettledSteps=ordered;
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
                    hasDefault|=item.GetProperty("match").ValueKind==JsonValueKind.Null;
                    var path=new HashSet<string>(assigned,StringComparer.Ordinal);ValidateCombFlow(item.GetProperty("body"),owned,path);paths.Add(path);
                }
                if(!hasDefault)paths.Add(new(assigned,StringComparer.Ordinal));
                if(paths.Count>0){var common=paths[0];foreach(var path in paths.Skip(1))common.IntersectWith(path);assigned.UnionWith(common);}break;
            case "repeat":
                var bodyAssigned=new HashSet<string>(assigned,StringComparer.Ordinal);ValidateCombFlow(statement.GetProperty("body"),owned,bodyAssigned);
                if(statement.GetProperty("count").GetInt32()>0)assigned.UnionWith(bodyAssigned);break;
            default: throw new IrValidationException("Combinational blocks require untimed blocking statements.");
        }
    }

    private static JsonElement Element(string json) { using var document=JsonDocument.Parse(json);return document.RootElement.Clone(); }

    private void EmitSettle(StringBuilder code)
    {
        foreach(var step in SettledSteps){code.AppendLine("{auto next=s;");int unique=0;Statement(code,step,ref unique);code.AppendLine("}");}
    }
}
