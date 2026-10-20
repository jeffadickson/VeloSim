using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

return await Build.MainAsync(args);

internal static class Build
{
    private static string Root = "";
    private static readonly string RunId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
    private static string Artifacts => Path.Combine(Root, "artifacts");
    private static string Native => Path.Combine(Artifacts, "native");
    private static string NativeCoverage => Path.Combine(Artifacts, "native-coverage");
    private static int Jobs;

    public static async Task<int> MainAsync(string[] args)
    {
        try
        {
            Root = FindRoot();
            Jobs = GetJobs();
            string command = args.Length == 0 ? "test" : args[0];
            if (command == "doctor")
            {
                if (args.Length != 1) throw new ArgumentException("usage: VeloSim.Build doctor");
                string path = Path.Combine(Artifacts, "doctor.json"); await BuildDiagnostics.WriteDoctorAsync(path, Jobs); Console.WriteLine(path); return 0;
            }
            if (command == "coverage-check")
            {
                if (args.Length > 2) throw new ArgumentException("usage: VeloSim.Build coverage-check [Cobertura.xml]");
                string path = args.Length == 2 ? Path.GetFullPath(args[1]) : File.ReadAllText(Path.Combine(Artifacts, "coverage", "latest.txt")).Trim();
                string result = BuildDiagnostics.CheckCoverage(path, BuildDiagnostics.Threshold(Environment.GetEnvironmentVariable("VELOSIM_MIN_MANAGED_LINE")), BuildDiagnostics.Threshold(Environment.GetEnvironmentVariable("VELOSIM_MIN_NATIVE_LINE")));
                Console.Write(result); return CoveragePassed(result) ? 0 : 1;
            }
            if (args.Length > 1 || command is not ("build" or "test" or "coverage" or "native-coverage" or "publish" or "help"))
                throw new ArgumentException("usage: VeloSim.Build [build|test|coverage|native-coverage|publish|help]");
            if (command == "help")
            {
                Console.WriteLine("VeloSim.Build: build, test, coverage, native-coverage, publish, doctor, coverage-check [Cobertura.xml]. Set VELOSIM_JOBS=1..256 to bound parallel builds.");
                return 0;
            }
            Console.WriteLine($"VeloSim {command}, Ninja parallel jobs={Jobs}");
            if (command == "native-coverage")
            {
                await Run("dotnet", ["build", "tests/VeloSim.Tests", "-c", "Release", $"-m:{Jobs}"]);
                await RunNativeCoverage(); return 0;
            }
            await ConfigureNative(Native, false);
            await Run("dotnet", ["build", "VeloSim.slnx", "-c", "Release", $"-m:{Jobs}"]);
            if (command == "build") return 0;
            if (command == "publish")
            {
                if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                    throw new PlatformNotSupportedException("The current native publishing target is linux-x64; run inside WSL Ubuntu.");
                foreach (string project in new[] { "src/VeloSim.Tools", "tools/VeloSim.Build" })
                    await Run("dotnet", ["publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-o", Path.Combine(Artifacts, "publish", Path.GetFileName(project)), $"-m:{Jobs}"]);
                foreach (string file in Directory.GetFiles(Path.Combine(Native, "lib"), "*.so"))
                    File.Copy(file, Path.Combine(Artifacts, "publish", "VeloSim.Tools", Path.GetFileName(file)), true);
                return 0;
            }
            await Run("ctest", ["--test-dir", Native, "--output-on-failure", "--parallel", Jobs.ToString(CultureInfo.InvariantCulture)]);
            string results = Path.Combine(Artifacts, "tests", RunId);
            var test = new List<string> { "test", "tests/VeloSim.Tests", "-c", "Release", "--no-build", "--results-directory", results, "--logger", "trx" };
            if (command == "coverage") test.AddRange(["--collect:XPlat Code Coverage", "--settings", "coverage.runsettings"]);
            await Run("dotnet", test, nativeDirectory: Path.Combine(Native, "lib"));
            if (command == "coverage")
            {
                bool managedMeasured = Directory.GetFiles(results, "coverage.cobertura.xml", SearchOption.AllDirectories)
                    .Select(XDocument.Load).Any(document => document.Descendants("package").Any(package =>
                        (string?)package.Attribute("name") == "VeloSim" && package.Descendants("line").Any()));
                if (!managedMeasured) throw new IOException("Managed coverage is empty; refusing to publish a native-only report as combined coverage.");
                await RunNativeCoverage();
                await Run("dotnet", ["tool", "restore"]);
                await Run("dotnet", ["tool", "run", "reportgenerator", $"-reports:{results}/**/coverage.cobertura.xml;{NativeCoverage}/native.lcov", $"-targetdir:{Artifacts}/coverage/{RunId}", "-reporttypes:Html;TextSummary;Cobertura"]);
                string report = Path.Combine(Artifacts, "coverage", RunId, "Cobertura.xml");
                string gate = BuildDiagnostics.CheckCoverage(report, BuildDiagnostics.Threshold(Environment.GetEnvironmentVariable("VELOSIM_MIN_MANAGED_LINE")), BuildDiagnostics.Threshold(Environment.GetEnvironmentVariable("VELOSIM_MIN_NATIVE_LINE")));
                await File.WriteAllTextAsync(Path.Combine(Artifacts, "coverage", RunId, "gate.json"), gate);
                if (!CoveragePassed(gate)) throw new IOException("Coverage thresholds failed; see gate.json.");
                await File.WriteAllTextAsync(Path.Combine(Artifacts, "coverage", "latest.txt"), report + "\n");
                Console.WriteLine($"Coverage report: {Artifacts}/coverage/{RunId}/index.html");
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Xml.XmlException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or TimeoutException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static bool CoveragePassed(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("status").GetString() == "pass";
    }

    private static async Task ConfigureNative(string directory, bool coverage)
    {
        var arguments = new List<string> { "-S", Root, "-B", directory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug", $"-DVELOSIM_COVERAGE={(coverage ? "ON" : "OFF")}" };
        if (coverage)
        {
            string cov = Llvm("llvm-cov");
            string compiler = Path.IsPathRooted(cov) ? Path.Combine(Path.GetDirectoryName(cov)!, "clang++") : "clang++";
            if (Path.IsPathRooted(compiler) && !File.Exists(compiler)) throw new IOException($"Matching Clang compiler missing: {compiler}");
            arguments.Add($"-DCMAKE_CXX_COMPILER={compiler}");
        }
        await Run("cmake", arguments);
        // Changing compilers can make CMake discard user cache options during
        // its automatic second configure. Never accept uninstrumented coverage.
        if (coverage && !File.ReadAllText(Path.Combine(directory, "CMakeCache.txt")).Contains("VELOSIM_COVERAGE:BOOL=ON", StringComparison.Ordinal))
        {
            await Run("cmake", arguments);
            if (!File.ReadAllText(Path.Combine(directory, "CMakeCache.txt")).Contains("VELOSIM_COVERAGE:BOOL=ON", StringComparison.Ordinal))
                throw new IOException("CMake did not enable requested native coverage.");
        }
        await Run("cmake", ["--build", directory, "--parallel", Jobs.ToString(CultureInfo.InvariantCulture)]);
    }

    private static async Task RunNativeCoverage()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("LLVM native coverage currently requires Linux/WSL.");
        await ConfigureNative(NativeCoverage, true);
        string profiles = Path.Combine(NativeCoverage, "profiles", RunId);
        Directory.CreateDirectory(profiles);
        var environment = new Dictionary<string, string> { ["LLVM_PROFILE_FILE"] = Path.Combine(profiles, "%m-%p.profraw") };
        await Run("ctest", ["--test-dir", NativeCoverage, "--output-on-failure", "--parallel", Jobs.ToString(CultureInfo.InvariantCulture)], environment: environment);
        await Run("dotnet", ["test", "tests/VeloSim.Tests", "-c", "Release", "--no-build"], nativeDirectory: Path.Combine(NativeCoverage, "lib"), environment: environment);
        string[] raw = Directory.GetFiles(profiles, "*.profraw");
        if (raw.Length == 0) throw new IOException("Native coverage produced no profiles.");
        string merged = Path.Combine(profiles, "merged.profdata");
        await Run(Llvm("llvm-profdata"), new[] { "merge", "-sparse", "-o", merged }.Concat(raw));
        string executable = Path.Combine(NativeCoverage, "bin", "velosim_native_tests");
        string[] objects = ["-object", Path.Combine(NativeCoverage, "lib", "libvelosim_ir.so"), "-object", Path.Combine(NativeCoverage, "lib", "libvelosim_scheduler.so"), "-object", Path.Combine(NativeCoverage, "bin", "velosim_json_tests")];
        await Run(Llvm("llvm-cov"), new[] { "export", executable, "-format=lcov", $"-instr-profile={merged}", "-ignore-filename-regex=/tests/|/external/" }.Concat(objects), output: Path.Combine(NativeCoverage, "native.lcov"));
        await Run(Llvm("llvm-cov"), new[] { "show", executable, "-format=html", $"-instr-profile={merged}", $"-output-dir={NativeCoverage}/html", "-ignore-filename-regex=/tests/|/external/" }.Concat(objects));
    }

    private static string Llvm(string tool)
    {
        string? directory = Environment.GetEnvironmentVariable("VELOSIM_LLVM_BIN");
        if (!string.IsNullOrEmpty(directory)) return Path.Combine(directory, tool);
        return File.Exists($"/usr/lib/llvm-18/bin/{tool}") ? $"/usr/lib/llvm-18/bin/{tool}" : tool;
    }

    private static async Task Run(string executable, IEnumerable<string> arguments, string? nativeDirectory = null, Dictionary<string, string>? environment = null, string? output = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (nativeDirectory != null)
        {
            start.Environment["VELOSIM_NATIVE_DIR"] = nativeDirectory;
            start.Environment["LD_LIBRARY_PATH"] = nativeDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        }
        if (environment != null) foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        Console.WriteLine($"> {executable} {string.Join(' ', start.ArgumentList)}");
        using var process = Process.Start(start) ?? throw new IOException($"could not start {executable}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            throw new TimeoutException($"{executable} exceeded 15 minutes");
        }
        string text = await stdout;
        string errors = await stderr;
        if (output == null) Console.Write(text); else await File.WriteAllTextAsync(output, text);
        Console.Error.Write(errors);
        if (process.ExitCode != 0) throw new IOException($"{executable} exited with code {process.ExitCode}");
    }

    private static int GetJobs()
    {
        string? value = Environment.GetEnvironmentVariable("VELOSIM_JOBS");
        if (value == null) return Math.Clamp(Environment.ProcessorCount, 1, 4);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int jobs) || jobs < 1 || jobs > 256)
            throw new ArgumentException("VELOSIM_JOBS must be an integer in 1..256");
        return jobs;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VeloSim.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new IOException("Run VeloSim.Build inside the repository.");
    }
}
