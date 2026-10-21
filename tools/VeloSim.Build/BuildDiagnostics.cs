using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public sealed record CoverageMeasurement(string Name, long Covered, long Total, long BranchCovered = 0, long BranchTotal = 0)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Covered / Total;
    public double? BranchPercent => BranchTotal == 0 ? null : 100.0 * BranchCovered / BranchTotal;
}
public static class BuildDiagnostics
{
    public static string ResolveTool(string name)
    {
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar))
            return File.Exists(Path.GetFullPath(name)) ? Path.GetFullPath(name) : throw new FileNotFoundException("Tool missing: " + name);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException("Tool missing from PATH: " + name);
    }
    public static async Task<string> ProbeVersionAsync(string executable, string argument, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + executable);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(); process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            throw new TimeoutException("Version probe timed out: " + executable);
        }
        if (process.ExitCode != 0) throw new IOException("Version probe failed: " + executable);
        return (await stdout + "\n" + await stderr).Trim();
    }
    public static async Task WriteDoctorAsync(string path, int jobs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("format", "velosim.build.doctor.v1");
            writer.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            writer.WriteString("architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            writer.WriteNumber("logical_processors", Environment.ProcessorCount); writer.WriteNumber("jobs", jobs);
            writer.WriteStartArray("tools");
            foreach (var name in new[] { "dotnet", "cmake", "ninja", "c++" })
            {
                var tool = ResolveTool(name); var version = await ProbeVersionAsync(tool, "--version", TimeSpan.FromSeconds(15));
                writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteString("path", tool); writer.WriteString("version", version); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, Encoding.UTF8.GetString(stream.ToArray()) + "\n");
    }
    public static IReadOnlyList<CoverageMeasurement> ReadCoverage(string path) => ReadCoverage(File.ReadAllBytes(path));
    private static IReadOnlyList<CoverageMeasurement> ReadCoverage(byte[] contents)
    {
        using var input = new MemoryStream(contents);
        var document = XDocument.Load(input); var packages = document.Root?.Element("packages")?.Elements("package").ToArray() ?? [];
        if (!packages.Any(p => (string?)p.Attribute("name") == "VeloSim")) throw new IOException("Coverage missing main VeloSim assembly.");
        if (!packages.Any(p => (string?)p.Attribute("name") == "VeloSim.Build" && p.Descendants("line").Any())) throw new IOException("Coverage missing compiled build helper assembly.");
        var nativeFiles = packages.Where(p => (string?)p.Attribute("name") == "Default").Elements("classes").Elements("class")
            .Where(c => c.Elements("lines").Elements("line").Any()).Select(c => ((string?)c.Attribute("filename") ?? "").Replace('\\', '/')).ToArray();
        foreach (var required in new[] { "native/ir/engine.cpp", "native/scheduler.cpp", "native/uhdm/json_escape.h", "native/rtl/runtime.h" })
            if (!nativeFiles.Any(file => file == required || file.EndsWith("/" + required, StringComparison.Ordinal))) throw new IOException("Coverage missing native file: " + required);
        CoverageMeasurement Read(string name, Func<XElement, bool> select)
        {
            var lines = packages.Where(select).SelectMany(p => p.Elements("classes").Elements("class").Elements("lines").Elements("line")).ToArray();
            if (lines.Length == 0) throw new IOException("Coverage missing measured " + name + " lines.");
            long covered = 0, branchCovered = 0, branchTotal = 0;
            foreach (var line in lines)
            {
                if (!long.TryParse((string?)line.Attribute("hits"), NumberStyles.None, CultureInfo.InvariantCulture, out var hits) || hits < 0) throw new IOException("Invalid coverage hits.");
                if (hits > 0) covered++;
                if ((string?)line.Attribute("branch") == "true")
                {
                    string text = (string?)line.Attribute("condition-coverage") ?? ""; int opening = text.LastIndexOf('('), slash = text.LastIndexOf('/'), closing = text.LastIndexOf(')');
                    if (opening < 0 || slash <= opening || closing <= slash || !long.TryParse(text.AsSpan(opening + 1, slash - opening - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bc) || !long.TryParse(text.AsSpan(slash + 1, closing - slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bt) || bt < 1 || bc < 0 || bc > bt) throw new IOException("Invalid branch coverage counters.");
                    branchCovered = checked(branchCovered + bc); branchTotal = checked(branchTotal + bt);
                }
            }
            return new(name, covered, lines.LongLength, branchCovered, branchTotal);
        }
        return [Read("managed", p => (string?)p.Attribute("name") is "VeloSim" or "VeloSim.Tools" or "VeloSim.Build"), Read("native", p => (string?)p.Attribute("name") == "Default")];
    }
    public static double Threshold(string? text)
    {
        if (text is null) return 0;
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value is < 0 or > 100) throw new ArgumentException("Coverage threshold must be in 0..100.");
        return value;
    }
    public static string CheckCoverage(string path, double managedMinimum, double nativeMinimum, double managedBranchMinimum = 0, double nativeBranchMinimum = 0)
    {
        if (new[] { managedMinimum, nativeMinimum, managedBranchMinimum, nativeBranchMinimum }.Any(v => !double.IsFinite(v) || v is < 0 or > 100)) throw new ArgumentException("Invalid coverage threshold.");
        var contents = File.ReadAllBytes(path); var measurements = ReadCoverage(contents);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("format", "velosim.coverage.gate.v1"); writer.WriteString("report", Path.GetFullPath(path));
            writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
            var passed = measurements[0].Percent >= managedMinimum && measurements[1].Percent >= nativeMinimum && (managedBranchMinimum == 0 || measurements[0].BranchPercent >= managedBranchMinimum) && (nativeBranchMinimum == 0 || measurements[1].BranchPercent >= nativeBranchMinimum);
            writer.WriteString("status", passed ? "pass" : "fail"); writer.WriteStartArray("measurements");
            foreach (var measurement in measurements)
            {
                writer.WriteStartObject(); writer.WriteString("name", measurement.Name); writer.WriteNumber("covered", measurement.Covered); writer.WriteNumber("total", measurement.Total); writer.WriteNumber("percent", measurement.Percent);
                writer.WriteNumber("minimum", measurement.Name == "managed" ? managedMinimum : nativeMinimum);
                writer.WriteNumber("branch_covered", measurement.BranchCovered); writer.WriteNumber("branch_total", measurement.BranchTotal);
                if (measurement.BranchPercent is { } branches) writer.WriteNumber("branch_percent", branches); else writer.WriteNull("branch_percent");
                writer.WriteNumber("branch_minimum", measurement.Name == "managed" ? managedBranchMinimum : nativeBranchMinimum); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
