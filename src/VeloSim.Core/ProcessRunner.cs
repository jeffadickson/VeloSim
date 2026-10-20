using System.Diagnostics;

namespace VeloSim;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
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
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
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
            throw new TimeoutException($"{executable} exceeded {timeout.TotalSeconds:g} seconds.");
        }
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
