using System.Text.Json;
namespace VeloSim;

public static class RtlExternalSdk
{
    public static async Task<string> ExportAsync(string ir, string top, string directory, CancellationToken token = default, RtlStateMode stateMode = RtlStateMode.FourState, RtlTargetLanguage target = RtlTargetLanguage.Cpp)
    {
        if (new FileInfo(ir).Length > 4 * 1024 * 1024) throw new ArgumentException("RTL IR exceeds 4 MiB.");
        string json = await File.ReadAllTextAsync(ir, token).ConfigureAwait(false);
        var design = RtlDesign.Parse(json);
        directory = Path.GetFullPath(directory);
        for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("External SDK output cannot traverse a symbolic link or reparse point.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new ArgumentException("External SDK output directory must be empty.");

        if (target == RtlTargetLanguage.CSharp)
            return await ExportCSharpAsync(design, json, top, directory, stateMode, token).ConfigureAwait(false);
        if (target == RtlTargetLanguage.Rust)
            return await ExportRustAsync(design, json, top, directory, stateMode, token).ConfigureAwait(false);

        string header = design.EmitModelHeader(top, stateMode);
        string? runtime = null;
        for (var parent = new DirectoryInfo(Environment.CurrentDirectory); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "native/rtl/external_model.h"))) { runtime = Path.Combine(parent.FullName, "native/rtl"); break; }
        if (runtime is null) throw new IOException("Run rtl-export from a VeloSim checkout containing native/rtl/external_model.h.");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "V" + top + ".h"), header, token).ConfigureAwait(false);
        foreach (string file in new[] { "runtime.h", "external_model.h", "vpi_user.h", "vpi.cpp" }) File.Copy(Path.Combine(runtime, file), Path.Combine(directory, file));
        if (design.Timed) File.Copy(Path.Combine(runtime, "hdl_scheduler.h"), Path.Combine(directory, "hdl_scheduler.h"));
        await File.WriteAllTextAsync(Path.Combine(directory, "design.rtl.json"), json, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory,"timing-diagnostics.txt"),
            "Conservative timed-process analysis; warnings do not prove races, and absence does not prove race freedom.\n"+string.Join('\n',design.TimingDiagnostics)+"\n",token).ConfigureAwait(false);
        string cmake = """
            cmake_minimum_required(VERSION 3.20)
            project(VeloSimExternal LANGUAGES C CXX)
            set(CMAKE_CXX_STANDARD 20)
            set(CMAKE_CXX_STANDARD_REQUIRED ON)
            set(CMAKE_BUILD_RPATH_USE_ORIGIN TRUE)
            add_library(velosim_vpi SHARED vpi.cpp)
            target_compile_definitions(velosim_vpi PRIVATE VELOSIM_VPI_BUILD)
            if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
              target_compile_options(velosim_vpi PRIVATE -fno-semantic-interposition)
            endif()
            target_link_libraries(velosim_vpi PRIVATE ${CMAKE_DL_LIBS})
            target_include_directories(velosim_vpi PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
            if(DEFINED VELOSIM_HOST_SOURCE)
              add_executable(velosim_host ${VELOSIM_HOST_SOURCE})
              target_link_libraries(velosim_host PRIVATE velosim_vpi)
            endif()
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "CMakeLists.txt"), cmake, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), """
            # VeloSim external model SDK

            Build from any directory with CMake and Ninja (C/C++20):
            cmake -S /path/to/sdk -B /path/to/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DVELOSIM_HOST_SOURCE=/absolute/path/to/host.cpp
            cmake --build /path/to/build --parallel 4

            The C++ word ABI is version2; recompile hosts and keep matching headers/runtime together.
            Include the generated V<top>.h in your host. Drive input port proxies,
            call eval() explicitly for settle/clock transitions, and final() once.
            One eval samples input changes as one event batch. The designated
            primary clock, when present, starts at zero; other logic inputs start X and bit inputs
            start zero (in --two-state mode, all signals start zero and X/Z inputs coerce to zero).
            Clockless models have no synthetic clock: eval() settles their DAG on startup and input changes.
            Bit inputs and destinations convert X/Z bits to zero.
            Use bits() for X/Z and up to 256-bit words; uint64 conversion rejects unknown or overflowing values.
            loadVpi() accepts a native plugin with a null-terminated exported
            vlog_startup_routines table before the first eval().
            The VPI subset supports signal lookup, metadata, identity, immediate
            input deposits, radix/vector/scalar values, and start/end/value/read-only
            callbacks. There is no Verilator/DPI ABI.
            Models whose manifest says explicit-time include hdl_scheduler.h.
            Their timeInc()/eval() host sequence executes queued older events
            before applying new input values at the host tick. Query eventsPending()
            and nextTimeSlot() on that generated model; advance by nextTimeSlot()-time().
            design.rtl.json time_precision, when present, is the decimal seconds
            exponent of a host tick. The bounded scheduler supports active/inactive/NBA,
            integer/runtime-integer and quantized decimal literal delays, signal waits
            and explicit/implicit sensitivity. Other SV regions, named fork scopes,
            timed calls, time-suffixed/runtime-real delays and general net resolution
            still require separate implementation/qualification.
            timing-diagnostics.txt records conservative process hazards. Warnings
            do not prove races; absence does not prove race freedom.
            Models and handles belong to their creating thread.
            Concatenation NBA targets require distinct destination words and at
            most 256 total assigned bits.
            Known bases for indexed reads and selected writes are limited to
            0..INT32_MAX; larger known bases throw before sequential updates commit.

            Linux/WSL GCC/Clang builds with unsigned __int128 limb products are qualified. Keep the host and shared runtime together
            when relocating the Linux build; the build RPATH uses $ORIGIN.
            Other platforms require separate qualification.
            external-model.json input paths are relative to this SDK directory;
            SHA-256 hashes cover the generated/copied input files, excluding the
            manifest itself and any files you add later.
            """, token).ConfigureAwait(false);
        var inputPaths = new List<string> { "V" + top + ".h", "runtime.h", "external_model.h", "vpi_user.h", "vpi.cpp", "design.rtl.json", "CMakeLists.txt", "README.md" };
        if (design.Timed) inputPaths.Add("hdl_scheduler.h");
        inputPaths.Add("timing-diagnostics.txt");
        var inputs = inputPaths.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(directory, p))).ToArray();
        string manifest = Path.Combine(directory, "external-model.json");
        var names = design.ExternalPortNames();
        bool twoState = stateMode == RtlStateMode.TwoState;
        string semantics = (twoState ? "two-state; " : "") + (design.Timed ? "bounded explicit-time processes; active/inactive/NBA; serial; explicit eval" : design.CombinationalOnly ? "clockless acyclic settling; serial; explicit eval" : "bounded input-edge processes; serial; explicit eval");
        var contract = new ExternalManifestContract("velosim.external.model.v2", 2, 256, top, "V" + top,
            semantics,
            design.Signals.Where(s => s.Direction != "internal").Select(s => new ExternalPortContract(s.Name, s.Width, s.Signed, twoState || s.TwoState, s.Direction, names[s.Name])).ToArray(),
            inputs.Select(input => new ExternalInputContract(Path.GetFileName(input.Path), input.Sha256)).ToArray());
        await File.WriteAllBytesAsync(manifest, JsonSerializer.SerializeToUtf8Bytes(contract, ManifestJsonContext.Default.ExternalManifestContract), token).ConfigureAwait(false);
        return manifest;
    }

    private static async Task<string> ExportCSharpAsync(RtlDesign design, string json, string top, string directory, RtlStateMode stateMode, CancellationToken token)
    {
        string modelCs = design.EmitCSharpModel(top, stateMode);
        string runtimeCs = RtlDesign.EmitCSharpRuntime();
        Directory.CreateDirectory(directory);
        string modelFile = "V" + top + ".cs";
        string csprojFile = "V" + top + ".csproj";
        await File.WriteAllTextAsync(Path.Combine(directory, modelFile), modelCs, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "VeloSimRuntime.cs"), runtimeCs, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, csprojFile), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <OptimizationPreference>Speed</OptimizationPreference>
              </PropertyGroup>
            </Project>
            """, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "design.rtl.json"), json, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), $"""
            # VeloSim C# model SDK (V{top})

            Zero-dependency .NET 11 library targeting VeloSim's register-resident {(stateMode == RtlStateMode.TwoState ? "2-state (W2)" : "4-state (W64)")} engine.
            Reference `{csprojFile}` from your C# testbench or build with:
            dotnet build {csprojFile} -c Release
            """, token).ConfigureAwait(false);
        var inputPaths = new[] { modelFile, "VeloSimRuntime.cs", csprojFile, "design.rtl.json", "README.md" };
        var inputs = inputPaths.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(directory, p))).ToArray();
        string manifest = Path.Combine(directory, "external-model.json");
        var names = design.CSharpPortNames();
        bool twoState = stateMode == RtlStateMode.TwoState;
        string semantics = $"csharp; {(twoState ? "two-state" : "four-state")}; {(design.CombinationalOnly ? "clockless acyclic settling; serial; explicit eval" : "bounded input-edge processes; serial; explicit eval")}";
        var contract = new ExternalManifestContract("velosim.external.model.v2", 2, 64, top, "V" + top, semantics,
            design.Signals.Where(s => s.Direction != "internal").Select(s => new ExternalPortContract(s.Name, s.Width, s.Signed, twoState || s.TwoState, s.Direction, names[s.Name])).ToArray(),
            inputs.Select(input => new ExternalInputContract(Path.GetFileName(input.Path), input.Sha256)).ToArray());
        await File.WriteAllBytesAsync(manifest, JsonSerializer.SerializeToUtf8Bytes(contract, ManifestJsonContext.Default.ExternalManifestContract), token).ConfigureAwait(false);
        return manifest;
    }

    private static async Task<string> ExportRustAsync(RtlDesign design, string json, string top, string directory, RtlStateMode stateMode, CancellationToken token)
    {
        string libRs = design.EmitRustModel(top, stateMode);
        string runtimeRs = RtlDesign.EmitRustRuntime();
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "lib.rs"), libRs, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "runtime.rs"), runtimeRs, token).ConfigureAwait(false);
        string crateName = "velosim_" + top.ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(directory, "Cargo.toml"), $"""
            [package]
            name = "{crateName}"
            version = "0.1.0"
            edition = "2021"

            [profile.release]
            opt-level = 3
            codegen-units = 1
            """, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "design.rtl.json"), json, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), $"""
            # VeloSim Rust model SDK (V{top})

            Zero-dependency, `#![forbid(unsafe_code)]` Rust 2021 crate targeting VeloSim's register-resident {(stateMode == RtlStateMode.TwoState ? "2-state (W2)" : "4-state (W64)")} engine.
            Build with:
            cargo build --release
            """, token).ConfigureAwait(false);
        var inputPaths = new[] { "Cargo.toml", "src/lib.rs", "src/runtime.rs", "design.rtl.json", "README.md" };
        var inputs = inputPaths.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(directory, p))).ToArray();
        string manifest = Path.Combine(directory, "external-model.json");
        var names = design.RustPortNames();
        bool twoState = stateMode == RtlStateMode.TwoState;
        string semantics = $"rust; {(twoState ? "two-state" : "four-state")}; {(design.CombinationalOnly ? "clockless acyclic settling; serial; explicit eval" : "bounded input-edge processes; serial; explicit eval")}";
        var contract = new ExternalManifestContract("velosim.external.model.v2", 2, 64, top, "V" + top, semantics,
            design.Signals.Where(s => s.Direction != "internal").Select(s => new ExternalPortContract(s.Name, s.Width, s.Signed, twoState || s.TwoState, s.Direction, names[s.Name])).ToArray(),
            inputs.Select(input => new ExternalInputContract(Path.GetRelativePath(directory, input.Path).Replace('\\', '/'), input.Sha256)).ToArray());
        await File.WriteAllBytesAsync(manifest, JsonSerializer.SerializeToUtf8Bytes(contract, ManifestJsonContext.Default.ExternalManifestContract), token).ConfigureAwait(false);
        return manifest;
    }
}
