using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    // Startup is a single ordered, untimed transaction. Input-dependent startup
    // would race a host/testbench drive, so it is outside this execution contract.
    private HashSet<string> ValidateInitialization(HashSet<string> combinationalWriters)
    {
        var writers=new HashSet<string>(StringComparer.Ordinal);
        if(Kind(Initialization)=="block"&&Initialization.GetProperty("items").GetArrayLength()==0){JsonShape.Fields(Initialization,["kind","items"]);return writers;}
        ValidateStatement(Initialization,writers,0);_=StatementCost(Initialization);
        var statements=Statements(Initialization).ToArray();
        if(statements.Any(e=>Kind(e) is "nba" or "process" or "parallel"))throw new IrValidationException("Initialization requires ordered untimed blocking statements.");
        var reads=statements.SelectMany(e=>new[]{"value","condition","count","target"}.Where(n=>e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Object).SelectMany(n=>References(e.GetProperty(n))));
        if(reads.Any(n=>signalIds.TryGetValue(n,out int id)&&Signals[id].Direction=="input"||combinationalWriters.Contains(n))||writers.Overlaps(combinationalWriters))throw new IrValidationException("Initialization cannot access inputs or combinational destinations.");
        return writers;
    }

    private void EmitInitialization(StringBuilder code)
    {
        code.AppendLine("{auto next=s;auto nextMem=mem;");int unique=0;
        Statement(code,Initialization,ref unique);
        code.AppendLine("s=std::move(next);mem=std::move(nextMem);}");
    }
}
