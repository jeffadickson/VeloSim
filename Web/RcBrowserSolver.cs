using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SchematicLab.Core;

namespace SchematicLab.Web;

/// <summary>Calls the same-origin local solver host from the browser.</summary>
internal sealed class RcBrowserSolver(HttpClient http)
{
    public async Task<IReadOnlyList<TransientSample>> SolveAsync(RcInput input, CancellationToken cancellation)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/rc", input, RcJson.Default.RcInput, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new RcSolverException("The RC solver host refused the request or is unavailable.");
            return await response.Content.ReadFromJsonAsync(RcJson.Default.TransientSampleArray, cancellation)
                ?? throw new RcSolverException("The RC solver host returned no waveform.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new RcSolverException("The RC solver host is unavailable or returned invalid data.");
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RcInput))]
[JsonSerializable(typeof(TransientSample[]))]
internal partial class RcJson : JsonSerializerContext;
