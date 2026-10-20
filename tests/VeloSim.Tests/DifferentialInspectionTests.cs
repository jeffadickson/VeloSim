using NUnit.Framework;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

public class DifferentialInspectionTests
{
    [Test] public async Task InspectionAcceptsIncompleteAndFailedReportsWithoutRequiringReplayArtifacts()
    {
        var incomplete = new CounterDifferentialReport(42, 2, 1, ["velosim", "icarus"], new Dictionary<string, IReadOnlyList<TraceSample>>(), new Dictionary<string, string> { ["velosim"] = "unsupported width", ["icarus"] = "dependency skipped" })
        {
            ObservedWidth = null, FailureStages = new Dictionary<string, string> { ["velosim"] = "frontend_compilation", ["icarus"] = "dependency_skipped" }
        };
        var failed = new CounterDifferentialReport(42, 1, 1, ["velosim", "icarus"], new Dictionary<string, IReadOnlyList<TraceSample>> { ["velosim"] = [new(0, "0")], ["icarus"] = [new(0, "1")] }, new Dictionary<string, string>()) { ObservedWidth = 1 };
        var directory = Path.Combine(Path.GetTempPath(), "velosim-inspection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            foreach (var report in new[] { incomplete, failed })
            {
                var path = Path.Combine(directory, report.Status + ".json"); File.WriteAllText(path, report.ToJson()); var output = new StringWriter(); var error = new StringWriter();
                Assert.That(await ToolCli.RunAsync(["inspect-report", path], output, error), Is.Zero, error.ToString());
                Assert.That(output.ToString(), Does.StartWith(report.Status).And.Contain("replay unavailable:"));
                Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate(report.ToJson()));
            }
            var node = JsonNode.Parse(failed.ToJson())!; node["status"] = "pass";
            Assert.Throws<IrValidationException>(() => DifferentialReportInspector.Inspect(node.ToJsonString()));
        }
        finally { Directory.Delete(directory, true); }
    }
}
