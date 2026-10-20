using NUnit.Framework;
using System.Text.Json;
namespace VeloSim.Tests;
public class BenchmarkCliTests
{
    [Test] public void BenchmarkCliValidatesOptionsAndPreservesExistingOutput()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.That(BenchmarkCli.Run(["--help"], output, error), Is.Zero);
        Assert.That(BenchmarkCli.Run(["--oops", "1"], output, error), Is.EqualTo(1));
        Assert.That(BenchmarkCli.Run(["--cycles"], output, error), Is.EqualTo(1));
        var file = Path.GetTempFileName(); try { File.WriteAllText(file, "keep"); Assert.That(BenchmarkCli.Run(["--report", file], output, error), Is.EqualTo(1)); Assert.That(File.ReadAllText(file), Is.EqualTo("keep")); } finally { File.Delete(file); }
    }
    [Test] public void BenchmarkCliWritesVerifiedReportAndReturnsCanceledStatus()
    {
        using var output = new StringWriter(); using var error = new StringWriter(); var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.That(BenchmarkCli.Run(["--processes", "2", "--cycles", "4", "--workers", "1,2", "--repeat", "1", "--warmup", "0", "--report", file], output, error), Is.Zero, error.ToString());
            using var json = JsonDocument.Parse(File.ReadAllText(file)); Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("pass"));
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); Assert.That(BenchmarkCli.Run([], output, error, canceled.Token), Is.EqualTo(130));
        }
        finally { File.Delete(file); }
    }
}
