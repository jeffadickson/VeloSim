using System.Text.Json.Nodes;
using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Public-IR regressions for the uncovered array validation and emission contracts.</summary>
[NonParallelizable]
public class RtlArrayMutationRegressionTests
{
    // Keep addresses unsigned and independent of allocation so fixed and dynamic
    // access validation can be challenged without a lifetime error masking it.
    private static JsonNode Fixture(bool shaped = false, int lower = 0)
    {
        var node = JsonNode.Parse(RtlDynamicArrayTests.Fixture())!;
        node["memories"]![0]!["dynamic"] = false;
        node["memories"]![0]!["depth"] = shaped ? 6 : 2;
        node["memories"]![0]!["lower"] = lower;
        if (shaped)
            node["memories"]![0]!["dimensions"] = new JsonArray(
                new JsonObject { ["left"] = 2, ["right"] = 1 },
                new JsonObject { ["left"] = 3, ["right"] = 5 });
        node["body"] = JsonNode.Parse("""
            {"kind":"nba","target":{"kind":"ref","name":"q"},
             "value":{"kind":"index","name":"m","index":{"kind":"const","bits":"01"}}}
            """);
        if (shaped)
        {
            var access = node["body"]!["value"]!.AsObject();
            access.Remove("index");
            access["indices"] = JsonNode.Parse("""[{"kind":"const","bits":"10"},{"kind":"const","bits":"11"}]""");
        }
        return node;
    }

    [Test]
    public void AccessSchemaRejectsUnknownFieldsBeforeEmission()
    {
        var node = Fixture();
        node["body"]!["value"]!["unexpected"] = true;
        Assert.That(() => RtlDesign.Parse(node.ToJsonString()), Throws.TypeOf<IrValidationException>());
    }

    [TestCase(false), TestCase(true)]
    public void ScalarAndCoordinateFormsCannotBeInterchanged(bool shaped)
    {
        var node = Fixture(shaped);
        var access = node["body"]!["value"]!.AsObject();
        access.Remove(shaped ? "indices" : "index");
        if (shaped) access["index"] = JsonNode.Parse("""{"kind":"const","bits":"01"}""");
        else access["indices"] = JsonNode.Parse("""[{"kind":"const","bits":"01"}]""");
        Assert.That(() => RtlDesign.Parse(node.ToJsonString()),
            Throws.TypeOf<IrValidationException>().With.Message.Contains("match its declared shape"));
    }

    [Test]
    public void CoordinateRankErrorExplainsTheRejectedAccess()
    {
        var node = Fixture(true);
        node["body"]!["value"]!["indices"]!.AsArray().RemoveAt(1);
        Assert.That(() => RtlDesign.Parse(node.ToJsonString()),
            Throws.TypeOf<IrValidationException>().With.Message.Contains("rank mismatch"));
    }

    [Test]
    public void FixedStorageRejectsLifetimeQueryWithUsefulDiagnostic()
    {
        var node = Fixture();
        node["body"]!["value"] = JsonNode.Parse("""{"kind":"size","name":"m"}""");
        Assert.That(() => RtlDesign.Parse(node.ToJsonString()),
            Throws.TypeOf<IrValidationException>().With.Message.Contains("require a dynamic array"));
    }

    [TestCase(false), TestCase(true)]
    public void CoordinateEmissionRetainsEveryValueAndDeclaredBound(bool sdk)
    {
        var design = RtlDesign.Parse(Fixture(true).ToJsonString());
        string source = sdk ? design.EmitModelHeader("dut") : design.EmitCpp();
        // Independent expected coordinates/bounds detect lost commas, expressions,
        // or dimension braces; merely checking for array_index missed these faults.
        Assert.That(source, Does.Contain("array_index({parse(\"10\"),parse(\"11\")},{{2,1},{3,5}})"));
    }

    [TestCase(0, false), TestCase(2, false), TestCase(0, true), TestCase(2, true)]
    public void LegacyLowerBoundIsAppliedOnlyWhenNonzero(int lower, bool sdk)
    {
        var design = RtlDesign.Parse(Fixture(lower: lower).ToJsonString());
        string source = sdk ? design.EmitModelHeader("dut") : design.EmitCpp();
        if (lower == 0)
        {
            Assert.That(source, Does.Contain("read(mem[0],parse(\"01\"),8)"));
            Assert.That(source, Does.Not.Contain("memory_index(parse(\"01\"),0)"));
        }
        else Assert.That(source, Does.Contain("memory_index(parse(\"01\"),2)"));
    }

    [Test]
    public void ExcessiveIndexNestingFailsAtThePublicJsonBoundary()
    {
        // JsonDocument's default maximum depth is 64. An expression's JSON object
        // nesting bounds validation recursion before its independent depth budget.
        string index = """{"kind":"const","bits":"0"}""";
        for (int i = 0; i < 70; i++) index = "{\"kind\":\"pos\",\"args\":[" + index + "]}";
        string json = Fixture().ToJsonString().Replace("{\"kind\":\"const\",\"bits\":\"01\"}", index, StringComparison.Ordinal);
        Assert.That(() => RtlDesign.Parse(json), Throws.TypeOf<IrValidationException>().With.Message.Contains("depth"));
    }
}
