using System.Diagnostics;
using System.Text;

namespace VeloSim;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed class ProcessTimeoutException(string message, string standardOutput, string standardError) : TimeoutException(message)
{
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
}
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default);
}
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        var outputText = new StringBuilder(); var errorText = new StringBuilder();
        var stdout = DrainAsync(process.StandardOutput, outputText, deadline.Token); var stderr = DrainAsync(process.StandardError, errorText, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            cancellationToken.ThrowIfCancellationRequested();
            string Tail(StringBuilder text) { var s = text.ToString(); return s.Length <= 4096 ? s : s[^4096..]; }
            throw new ProcessTimeoutException($"{executable} exceeded {timeout.TotalSeconds:g} seconds.\nstdout: {Tail(outputText)}\nstderr: {Tail(errorText)}", outputText.ToString(), errorText.ToString());
        }
        return new(process.ExitCode, outputText.ToString(), errorText.ToString());
    }
    private static async Task DrainAsync(StreamReader reader, StringBuilder destination, CancellationToken cancellationToken)
    {
        var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0) destination.Append(buffer, 0, count);
    }
}
