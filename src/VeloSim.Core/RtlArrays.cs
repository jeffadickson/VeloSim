using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    private static RtlMemory ParseMemory(JsonElement e)
    {
        JsonShape.Fields(e,["name","width","depth"],["signed","lower","dimensions","two_state","dynamic","queue"]);
        RtlDimension[]? dimensions=null;
        if(e.TryGetProperty("dimensions",out var shape))dimensions=shape.EnumerateArray().Select(d=>{JsonShape.Fields(d,["left","right"]);return new RtlDimension(d.GetProperty("left").GetInt32(),d.GetProperty("right").GetInt32());}).ToArray();
        return new(Name(e),e.GetProperty("width").GetInt32(),e.GetProperty("depth").GetInt32(),e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),e.TryGetProperty("lower",out var lower)?lower.GetInt32():0,dimensions,e.TryGetProperty("two_state",out var two)&&two.GetBoolean(),e.TryGetProperty("dynamic",out var dynamic)&&dynamic.GetBoolean(),e.TryGetProperty("queue",out var queue)&&queue.GetBoolean());
    }

    private static void ValidateMemoryShape(RtlMemory memory)
    {
        if(memory.Queue&&(memory.Dynamic||memory.Depth!=1||memory.LowerBound!=0||memory.Dimensions is not null))throw new IrValidationException("Queues use initially empty depth1 metadata without dynamic or fixed bounds.");
        if(memory.Dynamic&&(memory.Depth!=1||memory.LowerBound!=0||memory.Dimensions is not null))throw new IrValidationException("Dynamic arrays use depth1 metadata, no fixed bounds or dimensions, and initially empty storage.");
        if(memory.Dimensions is not {} shape)return;
        if(shape.Count is <1 or >4||memory.LowerBound!=0)throw new IrValidationException("Memory shape requires 1..4 dimensions and no legacy lower bound.");
        long product=1;
        foreach(var dimension in shape){if(dimension.Left<0||dimension.Right<0)throw new IrValidationException("Memory dimensions require nonnegative bounds.");product*=Math.Abs((long)dimension.Left-dimension.Right)+1;if(product>256)throw new IrValidationException("Memory dimension product exceeds 256.");}
        if(product!=memory.Depth)throw new IrValidationException("Memory depth does not match dimension product.");
    }

    private void RequireDynamic(string name)
    {
        if(!Memories[MemoryId(name)].Dynamic)throw new IrValidationException("Container lifetime operations require a dynamic array.");
    }

    private void RequireContainer(string name)
    {
        var memory=Memories[MemoryId(name)];
        if(!memory.Dynamic&&!memory.Queue)throw new IrValidationException("Container lifetime operations require a dynamic array or queue.");
    }

    private void RequireQueue(string name)
    {
        if(!Memories[MemoryId(name)].Queue)throw new IrValidationException("Queue operations require a queue declaration.");
    }

    // Pop is an ordered statement, never a side effect hidden inside an expression.
    // This keeps operand evaluation order explicit and preserves process ownership.
    private void ValidateQueueOperation(JsonElement e,HashSet<string> writers,int depth)
    {
        bool push=Kind(e)=="queue_push";
        JsonShape.Fields(e,push?["kind","name","front","value"]:["kind","name","front"],push?null:["target"]);
        RequireQueue(Name(e));_=e.GetProperty("front").GetBoolean();writers.Add(Name(e));
        if(push)ValidateExpression(e.GetProperty("value"),depth+1);
        else if(e.TryGetProperty("target",out var target)){
            ValidateExpression(target,depth+1);
            if(Kind(target)!="ref"||Signals[SignalId(Name(target))].Direction=="input"||Name(target)==Clock)throw new IrValidationException("Queue pop requires a writable whole-word destination.");
            writers.Add(Name(target));
        }
    }

    private static IEnumerable<JsonElement> MemoryIndices(JsonElement access)=>access.TryGetProperty("index",out var index)?[index]:access.GetProperty("indices").EnumerateArray().ToArray();
    private int ValidateMemoryAccess(JsonElement e,int depth)
    {
        JsonShape.Fields(e,["kind","name"],["index","indices"]);var memory=Memories[MemoryId(Name(e))];
        bool scalar=e.TryGetProperty("index",out _),multiple=e.TryGetProperty("indices",out _);
        if(scalar==multiple||scalar&&memory.Dimensions is not null||multiple&&memory.Dimensions is null)throw new IrValidationException("Memory access must match its declared shape.");
        var indices=MemoryIndices(e).ToArray();if(multiple&&indices.Length!=memory.Dimensions!.Count)throw new IrValidationException("Memory index rank mismatch.");
        foreach(var index in indices)ValidateIndex(index,depth+1);return memory.Width;
    }

    // Every coordinate is checked before linearization, so an invalid column
    // cannot accidentally address a valid cell in the next row.
    private string MemoryIndex(JsonElement access,int id)
    {
        var memory=Memories[id];var indices=MemoryIndices(access).ToArray();
        if(memory.Dimensions is {} shape)return "array_index({"+string.Join(',',indices.Select(i=>Expression(i)))+"},{"+string.Join(',',shape.Select(d=>$"{{{d.Left},{d.Right}}}"))+"})";
        return memory.LowerBound==0?Expression(indices[0]):$"memory_index({Expression(indices[0])},{memory.LowerBound})";
    }
}
