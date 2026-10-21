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
        var inputs = new[] { "V" + top + ".h", "runtime.h", "external_model.h", "vpi_user.h", "vpi.cpp", "design.rtl.json", "CMakeLists.txt" }.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(directory, p))).ToArray();
        string manifest = Path.Combine(directory, "external-model.json");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "velosim.external.model.v1"); writer.WriteString("top", top); writer.WriteString("model_class", "V" + top); writer.WriteString("execution_scope", "one known-clock posedge process; serial; explicit eval");
            writer.WriteStartArray("ports"); var names = design.ExternalPortNames();
            foreach (var signal in design.Signals.Where(s => s.Direction != "internal")) { writer.WriteStartObject(); writer.WriteString("name", signal.Name); writer.WriteNumber("width", signal.Width); writer.WriteString("direction", signal.Direction); writer.WriteString("member", names[signal.Name]); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteStartArray("inputs");
            foreach (var input in inputs) { writer.WriteStartObject(); writer.WriteString("path", input.Path); writer.WriteString("sha256", input.Sha256); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        await File.WriteAllBytesAsync(manifest, stream.ToArray(), token).ConfigureAwait(false);
        return manifest;
    }
}
