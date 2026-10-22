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
    [TestCase(51), TestCase(52), TestCase(53), TestCase(54), TestCase(55), TestCase(56), TestCase(57), TestCase(58), TestCase(59), TestCase(60)]
    public void DynamicTargetBoundary(int cycle)
    {
        var bit=new{kind="bitselect",value=Ref("q"),index=Ref("i")};
        object target=Concat(bit,Indexed("r",4,false)); string? error=null;
        switch(cycle)
        {
            case 52: target=Concat(bit,Indexed("r",63,true)); break;
            case 53: target=Concat(Ref("q"),Indexed("r",64,false)); error="64 bits"; break;
            case 54: target=Concat(bit,Indexed("r",0,false)); error="1..64"; break;
            case 55: target=Concat(bit,Indexed("r",-1,true)); error="1..64"; break;
            case 56: case 57: error=""; break;
            case 58: target=Concat(bit,new{kind="bitselect",value=Fixed("r",3,0),index=Ref("i")}); error="writable declared"; break;
            case 59: target=Concat(bit,new{kind="bitselect",value=Ref("clk"),index=Ref("i")}); error="writable declared"; break;
            case 60: error="conflicting"; break;
        }
        var node=Fixture(target);var selected=node["body"]!["items"]![1]!["target"]!["args"]![1]!;
        if(cycle==56)selected["descending"]="false";if(cycle==57)selected.AsObject().Remove("base");
        if(cycle==60)node["continuous"]=JsonSerializer.SerializeToNode(new[]{new{target=Ref("r"),value=Ref("a")}});
        if(error is null){var design=RtlDesign.Parse(node.ToJsonString());Assert.DoesNotThrow(()=>design.EmitCpp());Assert.DoesNotThrow(()=>design.EmitModelHeader("dynamic_concat"));}
        else Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain(error));
        TestContext.Progress.WriteLine($"CONCAT_CYCLE {cycle} PASS dynamic target boundary");
    }
    [TestCase(71), TestCase(72), TestCase(73), TestCase(74), TestCase(75), TestCase(76), TestCase(77), TestCase(78), TestCase(79), TestCase(80)]
    public void NestedTargetBoundary(int cycle)
    {
        object target=Concat(Concat(Ref("q"),Ref("r")),Ref("t")); int width=8;string? error=null;
        switch(cycle)
        {
            case 72: target=Concat(Ref("q"),Concat(Ref("r"),Ref("t")));break;
            case 73: target=Concat(Fixed("q",7,4),Concat(new{kind="bitselect",value=Ref("r"),index=Ref("i")},Indexed("t",4,true)));break;
            case 74: width=21;target=Concat(Ref("q"),Concat(Ref("r"),Fixed("t",21,0)));break;
            case 75: width=21;target=Concat(Ref("q"),Concat(Ref("r"),Fixed("t",22,0)));error="64 bits";break;
            case 76: target=Concat(Ref("q"),Concat(Ref("r"),Concat(Ref("t"),Ref("q"))));error="distinct destination";break;
            case 77: target=Concat(Ref("q"),Concat(Ref("r"),new{kind="const",bits="1"}));error="writable declared";break;
            case 78: target=Concat(Ref("q"),new{kind="cond",args=new[]{Ref("i"),Ref("r"),Ref("t")}});error="writable declared";break;
            case 79: error="";break;
        }
        var node=Fixture(target,width:width);
        if(cycle==79)node["body"]!["items"]![1]!["target"]!["extra"]=true;
        if(cycle==80){node["signals"]![1]!["name"]="nba0";foreach(var item in node["body"]!["items"]!.AsArray())item!["value"]!["name"]="nba0";}
        if(error is null){var design=RtlDesign.Parse(node.ToJsonString());Assert.DoesNotThrow(()=>design.EmitCpp());Assert.DoesNotThrow(()=>design.EmitModelHeader("nested_concat"));}
        else Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain(error));
        TestContext.Progress.WriteLine($"CONCAT_CYCLE {cycle} PASS nested target boundary");
    }
}
