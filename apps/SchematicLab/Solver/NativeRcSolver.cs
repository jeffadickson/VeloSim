using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SchematicLab.Core;

namespace SchematicLab.Solver;

/// <summary>Runs the native simulator's version 1 request for the bounded RC lesson.</summary>
public sealed partial class NativeRcSolver(string? executable)
{
    private readonly SemaphoreSlim slot = new(1);

    public async Task<IReadOnlyList<TransientSample>> SolveAsync(RcInput input, CancellationToken cancellation)
    {
        if (!input.IsValid) throw new RcSolverException("RC values are outside the supported range.");
        return await ExchangeAsync(Request(input), json => Parse(json, input.Backend), 256 * 1024, cancellation);
    }

    private async Task<T> ExchangeAsync<T>(string request, Func<string, T> parse, int outputLimit, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new RcSolverException("The host requires SPICE_SOLVER_PATH to point to the simulator executable.");
        if (!await slot.WaitAsync(0, cancellation))
            throw new RcSolverException("The solver is busy. Retry after the current run completes.");

        try { return await RunAsync(request, parse, outputLimit, cancellation); }
        finally { slot.Release(); }
    }

    private async Task<T> RunAsync<T>(string request, Func<string, T> parse, int outputLimit, CancellationToken cancellation)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var token = budget.Token;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };

        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new RcSolverException("The native solver could not start.");
            var output = ReadBoundedAsync(process.StandardOutput, outputLimit, token);
            var error = ReadBoundedAsync(process.StandardError, 16 * 1024, token);
            await process.StandardInput.WriteAsync(request.AsMemory(), token);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(token));
            var body = await output;
            if (process.ExitCode != 0)
            {
                using var refusal = JsonDocument.Parse(body);
                if (refusal.RootElement.TryGetProperty("error", out var failure) &&
                    failure.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String && message.GetString() is { Length: <= 512 } detail)
                    throw new RcSolverException(detail);
                throw new RcSolverException("The native solver refused the circuit or analysis.");
            }
            return parse(body);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new RcSolverException("The native solver exceeded its ten-second budget.");
        }
        catch (Exception error) when (error is IOException or Win32Exception or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            throw new RcSolverException("The native solver could not return a valid result.");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
    }

    private static string Request(RcInput input) => JsonSerializer.Serialize(new
    {
        schema_version = "1", case_id = "schematic-rc", backend = input.Backend.WireName(),
        circuit = new
        {
            nodes = new[] { "0", "in", "out" },
            elements = new object[]
            {
                new { kind = "voltage", id = "V1", p = 1, n = 0, volts = 0 },
                new { kind = "resistor", id = "R1", p = 1, n = 2, ohms = input.Ohms },
                new { kind = "capacitor", id = "C1", p = 2, n = 0, farads = input.Farads }
            }
        },
        analysis = new
        {
            kind = "transient_configured",
            options = new
            {
                stop_s = input.StopSeconds, max_step_s = input.TimeConstant / 100, max_steps = 1024,
                method = "trapezoidal",
                sources = new[]
                {
                    new { id = "V1", waveform = new
                    {
                        kind = "pulse", low = 0, high = input.Volts, delay_s = 0,
                        rise_s = input.RiseSeconds, fall_s = input.RiseSeconds,
                        width_s = 2 * input.StopSeconds, period_s = 4 * input.StopSeconds
                    }}
                }
            }
        }
    });

    private static IReadOnlyList<TransientSample> Parse(string json, SolverBackend backend)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CheckBackend(root, backend);
        if (root.GetProperty("schema_version").GetString() != "1" ||
            root.GetProperty("status").GetString() != "succeeded" || !root.GetProperty("complete").GetBoolean())
            throw new RcSolverException("The native solver returned an incomplete run.");

        var index = 0;
        foreach (var signal in root.GetProperty("signals").EnumerateArray())
        {
            if (signal.GetProperty("id").GetString() == "v(out)" && signal.GetProperty("unit").GetString() == "V")
                return root.GetProperty("samples").EnumerateArray().Select(sample =>
                    new TransientSample(sample.GetProperty("time_s").GetDouble(),
                        sample.GetProperty("values")[index].GetDouble())).ToArray();
            index++;
        }
        throw new RcSolverException("The solver did not return the selected voltage signal.");
    }

    /// <summary>Do not accept a silent fallback or a host that ignored provider selection.</summary>
    internal static void CheckBackend(JsonElement root, SolverBackend expected)
    {
        if (!Enum.IsDefined(expected) || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("backend", out var backend) ||
            backend.ValueKind != JsonValueKind.Object || !backend.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String || id.GetString() != expected.ProviderId())
            throw new RcSolverException("The native solver returned a missing or different linear provider.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            if (text.Length + count > limit) throw new RcSolverException("The native solver output exceeded its limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
