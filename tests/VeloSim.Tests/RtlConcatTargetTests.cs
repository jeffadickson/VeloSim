using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class RtlConcatTargetTests
{
    internal static object Ref(string name) => new { kind = "ref", name };
    internal static object Concat(object a, object b) => new { kind = "concat", args = new[] { a, b } };
    internal static object Fixed(string name, int left, int right) => new { kind = "partselect", value = Ref(name), left, right };
    internal static object Indexed(string name, int width, bool descending) => new { kind = "indexedselect", value = Ref(name), @base = Ref("i"), width, descending };
    internal static JsonNode Fixture(object target, object? value = null, int width = 8) => JsonNode.Parse(JsonSerializer.Serialize(new { format = "velosim.rtl.v1", signals = new[] { new { name = "clk", width = 1, direction = "input" }, new { name = "a", width = 64, direction = "input" }, new { name = "i", width = 64, direction = "input" }, new { name = "q", width, direction = "output" }, new { name = "r", width, direction = "output" }, new { name = "t", width, direction = "output" } }, memories = Array.Empty<object>(), continuous = Array.Empty<object>(), clock = "clk", body = new { kind = "block", items = new object[] { new { kind = "nba", target = Ref("t"), value = Ref("a") }, new { kind = "nba", target, value = value ?? Ref("a") } } } }))!;
    [TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5), TestCase(6), TestCase(7), TestCase(8), TestCase(9), TestCase(10)]
    public void ConcatTargetValidation(int cycle)
    {
        object target = Concat(Ref("q"), Ref("r")); int width = 8; string? error = null;
        switch (cycle)
        {
            case 2: target = Concat(Ref("q"), Concat(Ref("r"),Ref("t"))); break;
            case 3: width = 32; break;
            case 4: width = 33; error = "64 bits"; break;
            case 5: target = Concat(Ref("q"),Ref("q")); error = "distinct destination"; break;
            case 6: target = Concat(Ref("q"),Fixed("a",3,0)); error = "writable declared"; break;
            case 7: target = Concat(Ref("q"),Ref("clk")); error = "writable declared"; break;
            case 8: target = Concat(Ref("q"),new {kind="add",args=new[]{Ref("r"),Ref("t")}}); error = "writable declared"; break;
            case 9: target = Concat(Ref("q"),new {kind="index",name="m",index=new {kind="const",bits="0"}}); error = "writable declared"; break;
            case 10: error = "conflicting"; break;
        }
        var node = Fixture(target,width:width);
        if(cycle==9)node["memories"]=JsonSerializer.SerializeToNode(new[]{new{name="m",width=8,depth=2}});
        if(cycle==10)node["continuous"]=JsonSerializer.SerializeToNode(new[]{new{target=Ref("r"),value=Ref("a")}});
        if(error is null) { var design=RtlDesign.Parse(node.ToJsonString()); Assert.DoesNotThrow(()=>design.EmitCpp()); Assert.DoesNotThrow(()=>design.EmitModelHeader("concat_target")); }
        else Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain(error));
        TestContext.Progress.WriteLine($"CONCAT_CYCLE {cycle} PASS validation");
    }
    [TestCase(31), TestCase(32), TestCase(33), TestCase(34), TestCase(35), TestCase(36), TestCase(37), TestCase(38), TestCase(39), TestCase(40)]
    public void InvalidConcatTargetBoundary(int cycle)
    {
        object target=Concat(Ref("q"),Ref("r")); string error="distinct destination";
        switch(cycle)
        {
            case 31: target=Concat(Fixed("q",3,0),Fixed("q",7,4)); break;
            case 32: target=Concat(Ref("q"),new{kind="bitselect",value=Ref("q"),index=Ref("i")}); break;
            case 33: target=Concat(Indexed("q",4,false),Indexed("q",4,true)); break;
            case 34: target=Concat(Ref("q"),Concat(Ref("r"),Ref("q"))); break;
            case 35: target=new{kind="concat",args=Array.Empty<object>()}; error="expression"; break;
            case 36: target=new{kind="concat",args=new[]{Ref("q")}}; error="expression"; break;
            case 37: target=new{kind="concat",args=new[]{Ref("q"),Ref("r"),Ref("t")}}; error="expression"; break;
            case 38: target=Concat(Ref("q"),Ref("missing")); error="signal"; break;
            case 39: target=Concat(Ref("q"),new{kind="partselect",value=new{kind="index",name="m",index=new{kind="const",bits="0"}},left=3,right=0}); error="writable declared"; break;
            case 40: target=new{kind="concat"}; error=""; break;
        }
        var node=Fixture(target);if(cycle==39)node["memories"]=JsonSerializer.SerializeToNode(new[]{new{name="m",width=8,depth=2}});
        Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain(error).IgnoreCase);
        TestContext.Progress.WriteLine($"CONCAT_CYCLE {cycle} PASS invalid target boundary");
    }
}
