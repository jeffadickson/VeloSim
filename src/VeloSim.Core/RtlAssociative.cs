using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed partial class RtlDesign
{
    private IEnumerable<string> StatementReferences(JsonElement e)
    {
        foreach(string field in new[]{"value","target","condition","count","index"})
            if(e.TryGetProperty(field,out var expression)&&expression.ValueKind==JsonValueKind.Object)
                foreach(var name in References(expression))yield return name;
        if(Kind(e)=="assoc_traverse")yield return Name(e);
    }
    private static void ValidateAssociativeMetadata(RtlMemory memory)
    {
        if(!memory.Associative){if(memory.KeyWidth!=0||memory.KeySigned)throw new IrValidationException("Key metadata requires an associative array.");return;}
        if(memory.Dynamic||memory.Queue||memory.Depth!=1||memory.LowerBound!=0||memory.Dimensions is not null||memory.KeyWidth is <1 or >256)
            throw new IrValidationException("Associative arrays require initially empty depth1 metadata and an integral 1..256-bit key type.");
    }

    private void RequireAssociative(string name)
    {
        if(!Memories[MemoryId(name)].Associative)throw new IrValidationException("Associative operation requires an associative declaration.");
    }

    private void ValidateAssociativeTraversal(JsonElement e,HashSet<string> writers,int depth)
    {
        JsonShape.Fields(e,["kind","name","operation","index"],["target"]);RequireAssociative(Name(e));
        var memory=Memories[MemoryId(Name(e))];
        if(e.GetProperty("operation").GetString() is not ("first" or "last" or "next" or "prev"))throw new IrValidationException("Unsupported associative traversal.");
        ValidateExpression(e.GetProperty("index"),depth+1);
        string Writable(JsonElement target){
            if(Kind(target)!="ref")throw new IrValidationException("Associative traversal requires whole-word destinations.");
            string name=Name(target);if(name==Clock||Signals[SignalId(name)].Direction=="input")throw new IrValidationException("Associative traversal destination must be writable.");return name;
        }
        string index=Writable(e.GetProperty("index"));var variable=Signals[SignalId(index)];
        if(variable.Width!=memory.KeyWidth||variable.Signed!=memory.KeySigned)throw new IrValidationException("Traversal index must match its associative key width and signedness.");
        writers.Add(index);
        if(e.TryGetProperty("target",out var target)){string result=Writable(target);if(result==index)throw new IrValidationException("Traversal index and result must be distinct.");writers.Add(result);}
    }

    private static IEnumerable<string> AssociativeDestinations(IEnumerable<JsonElement> statements)
    {
        foreach(var e in statements.Where(e=>Kind(e)=="assoc_traverse")){
            yield return Name(e.GetProperty("index"));
            if(e.TryGetProperty("target",out var target))yield return Name(target);
        }
    }

    private void EmitAssociativeTraversal(StringBuilder code,JsonElement e,ref int unique)
    {
        int id=MemoryId(Name(e));var memory=Memories[id];var target=e.GetProperty("index");int indexId=SignalId(Name(target));
        int operation=e.GetProperty("operation").GetString() switch{"first"=>0,"last"=>1,"next"=>2,_=>3};
        string key="assoc_key"+unique++,result="assoc_result"+unique++;
        code.AppendLine($"{{auto {key}=s[{indexId}];auto {result}=assoc_traverse(assoc[{id}],{key},{memory.KeyWidth},{(memory.KeySigned?"true":"false")},{operation});");
        WordWrite(code,target,key);code.AppendLine($"s[{indexId}]=next[{indexId}];");
        if(e.TryGetProperty("target",out var output)){WordWrite(code,output,$"signed_resize({result},{ExpressionWidth(output)})");int outputId=SignalId(Name(output));code.AppendLine($"s[{outputId}]=next[{outputId}];");}
        else code.AppendLine($"(void){result};");
        code.AppendLine("}");
    }
}
