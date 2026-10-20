using System.Globalization;

namespace VeloSim;

public sealed class VerilatorReference(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<IReadOnlyList<TraceSample>> RunAsync(IReadOnlyList<string> sources, string top, string outputDirectory, int cycles, int width, int buildJobs, string? compiler = null, CancellationToken cancellationToken = default, string? cmake = null, string? ninja = null, string? cxx = null, bool requireWidthHeader = false)
    {
        if (buildJobs is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(buildJobs));
        if (sources.Count == 0 || sources.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(top)) throw new ArgumentException("Reference sources and top required.");
        if (cycles < 1 || width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(cycles));
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory); var objectDirectory = Path.Combine(directory, "obj_dir");
        if (Directory.Exists(objectDirectory) || File.Exists(Path.Combine(directory, "CMakeLists.txt"))) throw new IOException("Verilator build artifacts already exist.");
        var verilator = ArtifactMetadata.ResolveExecutable(compiler ?? "verilator", directory) ?? compiler ?? "verilator";
        var ninjaPath = ArtifactMetadata.ResolveExecutable(ninja ?? "ninja", directory) ?? ninja ?? "ninja";
        var cxxPath = ArtifactMetadata.ResolveExecutable(cxx ?? "c++", directory) ?? cxx ?? "c++";
        var script = GenerateCMake(top, sources.Select(Path.GetFullPath).ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "CMakeLists.txt"), script, cancellationToken).ConfigureAwait(false);
        var configure = new[] { "-S", directory, "-B", objectDirectory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_MAKE_PROGRAM=" + ninjaPath, "-DCMAKE_CXX_COMPILER=" + cxxPath, "-DVERILATOR_BIN=" + verilator };
        var compilation = await runner.RunAsync(cmake ?? "cmake", configure, directory, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(compilation, "Verilator CMake configuration");
        var build = await runner.RunAsync(cmake ?? "cmake", ["--build", objectDirectory, "--parallel", buildJobs.ToString(CultureInfo.InvariantCulture)], directory, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(build, "Verilator Ninja build");
        var executable = Path.Combine(objectDirectory, "reference" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(executable)) throw new IOException("Verilator produced no executable.");
        var result = await runner.RunAsync(executable, [], directory, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(result, "Verilator execution");
        return IcarusReference.ParseOutput(result.StandardOutput, cycles, width, requireWidthHeader);
    }
    public static string GenerateCMake(string top, IReadOnlyList<string> sources)
    {
        CounterTestbench.Generate(top, 8, [0]);
        string Quote(string value)
        {
            if (value.Contains(';') || value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("CMake source paths cannot contain semicolons or newlines.");
            value = value.Replace('\\', '/'); var equals = "";
            while (value.Contains("]" + equals + "]", StringComparison.Ordinal)) equals += "=";
            return "[" + equals + "[" + value + "]" + equals + "]";
        }
        return "cmake_minimum_required(VERSION 3.20)\nproject(VeloSimReference LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nfind_package(verilator REQUIRED)\nadd_executable(reference)\nverilate(reference PREFIX V" + top + " TOP_MODULE " + top + " SOURCES\n" + string.Join('\n', sources.Select(Quote)) + "\nVERILATOR_ARGS --timing --main -Wno-fatal)\n";
    }
}
