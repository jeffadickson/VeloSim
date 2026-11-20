using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed partial class RtlDesign
{
    private static IEnumerable<string> QueueDestinations(IEnumerable<JsonElement> statements)
    {
        foreach(var statement in statements.Where(e=>Kind(e) is "queue_push" or "queue_pop")){
            yield return Name(statement);
            if(statement.TryGetProperty("target",out var target))yield return Name(target);
        }
    }

    private void EmitQueueOperation(StringBuilder code,JsonElement e,ref int unique)
    {
        int id=MemoryId(Name(e));var memory=Memories[id];
        string twoState=memory.TwoState?"true":"false",front=e.GetProperty("front").GetBoolean()?"true":"false";
        if(Kind(e)=="queue_push")code.AppendLine($"queue_push(nextMem[{id}],{Expression(e.GetProperty("value"),memory.Width)},{memory.Width},{twoState},{front});");
        else{
            string result="queue_value"+unique++;
            code.AppendLine($"auto {result}=queue_pop(nextMem[{id}],{memory.Width},{twoState},{front});");
            if(e.TryGetProperty("target",out var target)){
                string value=memory.Signed?$"signed_resize({result},{ExpressionWidth(target)})":result;
                WordWrite(code,target,value);
                code.AppendLine($"s[{SignalId(Name(target))}]=next[{SignalId(Name(target))}];");
            }else code.AppendLine($"(void){result};");
        }
        // Subsequent operations in this process observe the changed lifetime;
        // another process cannot read or mutate this queue under isolation rules.
        code.AppendLine($"mem[{id}]=nextMem[{id}];");
    }
}
