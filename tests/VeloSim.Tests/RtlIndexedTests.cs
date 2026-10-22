using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class RtlIndexedTests
{
    private static object Ref(string name) => new { kind = "ref", name };
    private static string Fixture(object value, object? target = null) => JsonSerializer.Serialize(new { format = "velosim.rtl.v1", signals = new[] { new { name = "clk", width = 1, direction = "input" }, new { name = "a", width = 64, direction = "input" }, new { name = "i", width = 64, direction = "input" }, new { name = "q", width = 64, direction = "output" } }, memories = Array.Empty<object>(), continuous = Array.Empty<object>(), clock = "clk", body = new { kind = "nba", target = target ?? Ref("q"), value } });
    [TestCase(11), TestCase(12), TestCase(13), TestCase(14), TestCase(15), TestCase(16), TestCase(17), TestCase(18), TestCase(19), TestCase(20)]
    public void IndexedReadIrCycle(int id)
    {
        var node = JsonNode.Parse(Fixture(new { kind = "indexedselect", value = Ref("a"), @base = Ref("i"), width = 4, descending = false }))!;
        var selected = node["body"]!["value"]!;
        switch (id)
        {
            case 11: Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(), Does.Contain("select_indexed(s[1],s[2],4,false)")); break;
            case 12: selected["descending"] = true; node["signals"]![1]!["name"] = "select_indexed"; selected["value"]!["name"] = "select_indexed"; var parsed = RtlDesign.Parse(node.ToJsonString()); Assert.That(parsed.EmitModelHeader("indexed"), Does.Contain("select_indexed(s[1],s[2],4,true)")); Assert.That(parsed.ExternalPortNames()["select_indexed"], Is.Not.EqualTo("select_indexed")); break;
            case 13: selected["width"] = 0; break;
            case 14: selected["width"] = 65; break;
            case 15: selected["width"] = -1; break;
            case 16: selected["extra"] = 1; break;
            case 17: selected.AsObject().Remove("base"); break;
            case 18: selected["descending"] = "yes"; break;
            case 19: case 20:
                var expression = selected.DeepClone(); expression["base"] = JsonSerializer.SerializeToNode(Ref(id == 19 ? "clk" : "q"));
                node["body"] = new JsonObject { ["kind"] = "block", ["items"] = new JsonArray() };
                node["continuous"] = new JsonArray(new JsonObject { ["target"] = JsonSerializer.SerializeToNode(Ref("q")), ["value"] = expression }); break;
        }
        if (id >= 13) Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
        TestContext.Progress.WriteLine($"RTL_CYCLE {id} PASS indexed-read IR");
    }
    [TestCase(41), TestCase(42), TestCase(43), TestCase(44), TestCase(45), TestCase(46), TestCase(47), TestCase(48), TestCase(49), TestCase(50)]
    public void SelectedWriteIrCycle(int id)
    {
        object target = id == 42 ? new { kind = "partselect", value = Ref("q"), left = 3, right = 0 } : new { kind = "bitselect", value = Ref("q"), index = Ref("i") };
        var node = JsonNode.Parse(Fixture(Ref("a"), target))!; var lhs = node["body"]!["target"]!;
        switch (id)
        {
            case 41: Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(), Does.Contain("deposit_indexed(next[3],s[2],1,false,s[1])")); break;
            case 42: Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitModelHeader("selected"), Does.Contain("deposit_indexed(next[3],W{0,0,0,64},4,false,s[1])")); break;
            case 43: lhs["value"]!["name"] = "a"; break;
            case 44: lhs["value"]!["name"] = "clk"; break;
            case 45: lhs["value"] = JsonSerializer.SerializeToNode(new { kind = "bitselect", value = Ref("q"), index = Ref("i") }); break;
            case 46: lhs["value"] = JsonSerializer.SerializeToNode(new { kind = "const", bits = "0000" }); break;
            case 47: lhs["extra"] = true; break;
            case 48: lhs["index"] = JsonSerializer.SerializeToNode(Ref("missing")); break;
            case 49: node["continuous"] = new JsonArray(new JsonObject { ["target"] = JsonSerializer.SerializeToNode(Ref("q")), ["value"] = JsonSerializer.SerializeToNode(Ref("a")) }); break;
            case 50:
                node["signals"]![1]!["name"] = "deposit_indexed"; node["body"]!["value"]!["name"] = "deposit_indexed";
                var parsed = RtlDesign.Parse(node.ToJsonString()); Assert.That(parsed.ExternalPortNames()["deposit_indexed"], Is.Not.EqualTo("deposit_indexed")); Assert.That(parsed.EmitModelHeader("alias"), Does.Contain("deposit_indexed(next[3]")); break;
        }
        if (id is >= 43 and <= 49) Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
        TestContext.Progress.WriteLine($"RTL_CYCLE {id} PASS selected-word IR");
    }    [TestCase(61), TestCase(62), TestCase(63), TestCase(64), TestCase(65), TestCase(66), TestCase(67), TestCase(68), TestCase(69), TestCase(70)]
    public void IndexedWriteIrCycle(int id)
    {
        var node = JsonNode.Parse(Fixture(Ref("a"), new { kind = "indexedselect", value = Ref("q"), @base = Ref("i"), width = 4, descending = false }))!; var lhs = node["body"]!["target"]!;
        switch (id)
        {
            case 61: Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(), Does.Contain("deposit_indexed(next[3],s[2],4,false,s[1])")); break;
            case 62: lhs["descending"] = true; Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitModelHeader("indexed_write"), Does.Contain("deposit_indexed(next[3],s[2],4,true,s[1])")); break;
            case 63: lhs["width"] = 1; Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(), Does.Contain("deposit_indexed(next[3],s[2],1,false")); break;
            case 64: lhs["width"] = 64; Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(), Does.Contain("deposit_indexed(next[3],s[2],64,false")); break;
            case 65: lhs["width"] = 0; break;
            case 66: lhs["width"] = 65; break;
            case 67: lhs["width"] = -1; break;
            case 68: lhs["descending"] = 1; break;
            case 69: lhs.AsObject().Remove("base"); break;
            case 70:
                node["body"]!["value"] = JsonSerializer.SerializeToNode(new { kind = "shl", args = new object[] { new { kind = "bitselect", value = Ref("a"), index = new { kind = "const", bits = "0" } }, new { kind = "const", bits = "11" } } });
                Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(), Does.Contain("shift(select_bit(s[1],parse(\"0\")),parse(\"11\"),4,false)")); break;
        }
        if (id is >= 65 and <= 69) Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
        TestContext.Progress.WriteLine($"RTL_CYCLE {id} PASS indexed-word NBA IR");
    }}
