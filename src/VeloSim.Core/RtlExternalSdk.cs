using System.Text.Json;
namespace VeloSim;

public static class RtlExternalSdk
{
    public static async Task<string> ExportAsync(string ir, string top, string directory, CancellationToken token = default)
    {
        if (new FileInfo(ir).Length > 4 * 1024 * 1024) throw new ArgumentException("RTL IR exceeds 4 MiB.");
        string json = await File.ReadAllTextAsync(ir, token).ConfigureAwait(false);
        var design = RtlDesign.Parse(json);
        string header = design.EmitModelHeader(top);
        string? runtime = null;
        for (var parent = new DirectoryInfo(Environment.CurrentDirectory); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "native/rtl/external_model.h"))) { runtime = Path.Combine(parent.FullName, "native/rtl"); break; }
        if (runtime is null) throw new IOException("Run rtl-export from a VeloSim checkout containing native/rtl/external_model.h.");
        directory = Path.GetFullPath(directory);
        for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("External SDK output cannot traverse a symbolic link or reparse point.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new ArgumentException("External SDK output directory must be empty.");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "V" + top + ".h"), header, token).ConfigureAwait(false);
        foreach (string file in new[] { "runtime.h", "external_model.h", "vpi_user.h", "vpi.cpp" }) File.Copy(Path.Combine(runtime, file), Path.Combine(directory, file));
        await File.WriteAllTextAsync(Path.Combine(directory, "design.rtl.json"), json, token).ConfigureAwait(false);
        string cmake = """
            cmake_minimum_required(VERSION 3.20)
            project(VeloSimExternal LANGUAGES C CXX)
            set(CMAKE_CXX_STANDARD 20)
            set(CMAKE_CXX_STANDARD_REQUIRED ON)
            set(CMAKE_BUILD_RPATH_USE_ORIGIN TRUE)
            add_library(velosim_vpi SHARED vpi.cpp)
            target_compile_definitions(velosim_vpi PRIVATE VELOSIM_VPI_BUILD)
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

            Include the generated V<top>.h in your host. Drive input port proxies,
            call eval() explicitly for settle/clock transitions, and final() once.
            Use bits() for X/Z; integer conversion rejects unknown values.
            loadVpi() accepts a native plugin with a null-terminated exported
            vlog_startup_routines table before the first eval().
            The VPI subset supports signal lookup, metadata, identity, immediate
            input deposits, radix/vector/scalar values, and start/end/value/read-only
            callbacks. There is no HDL delay scheduler or Verilator/DPI ABI.
            Models and handles belong to their creating thread.

            Linux/WSL builds are qualified. Keep the host and shared runtime together
            when relocating the Linux build; the build RPATH uses $ORIGIN.
            Other platforms require separate qualification.
            external-model.json input paths are relative to this SDK directory;
            SHA-256 hashes cover the generated/copied input files, excluding the
            manifest itself and any files you add later.
            """, token).ConfigureAwait(false);
        var inputs = new[] { "V" + top + ".h", "runtime.h", "external_model.h", "vpi_user.h", "vpi.cpp", "design.rtl.json", "CMakeLists.txt", "README.md" }.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(directory, p))).ToArray();
        string manifest = Path.Combine(directory, "external-model.json");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "velosim.external.model.v1"); writer.WriteString("top", top); writer.WriteString("model_class", "V" + top); writer.WriteString("execution_scope", "one known-clock posedge process; serial; explicit eval");
            writer.WriteStartArray("ports"); var names = design.ExternalPortNames();
            foreach (var signal in design.Signals.Where(s => s.Direction != "internal")) { writer.WriteStartObject(); writer.WriteString("name", signal.Name); writer.WriteNumber("width", signal.Width); writer.WriteString("direction", signal.Direction); writer.WriteString("member", names[signal.Name]); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteStartArray("inputs");
            foreach (var input in inputs) { writer.WriteStartObject(); writer.WriteString("path", Path.GetFileName(input.Path)); writer.WriteString("sha256", input.Sha256); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        await File.WriteAllBytesAsync(manifest, stream.ToArray(), token).ConfigureAwait(false);
        return manifest;
    }
}
