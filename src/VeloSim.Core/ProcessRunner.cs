using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

namespace VeloSim;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed class ProcessOutputLimitException(string message, string standardOutput, string standardError) : IOException(message)
{
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
}
public sealed class ProcessTimeoutException(string message, string standardOutput, string standardError) : TimeoutException(message)
{
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
}
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default);
}
public sealed partial class ProcessRunner : IProcessRunner
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillGroup(int pid, int signal);
    public int MaxCharactersPerStream { get; }
    public ProcessRunner(int maxCharactersPerStream = 16 * 1024 * 1024)
    {
        if (maxCharactersPerStream is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxCharactersPerStream));
        MaxCharactersPerStream = maxCharactersPerStream;
    }
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        bool ownGroup = OperatingSystem.IsLinux();
        if (ownGroup)
        {
            if (!File.Exists("/usr/bin/setsid")) throw new IOException("Linux process isolation requires /usr/bin/setsid.");
            start.FileName = "/usr/bin/setsid"; start.ArgumentList.Add("--wait"); start.ArgumentList.Add("--"); start.ArgumentList.Add(executable);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        var outputText = new StringBuilder(); var errorText = new StringBuilder();
        int exceeded = 0;
        void LimitExceeded() { Interlocked.Exchange(ref exceeded, 1); deadline.Cancel(); }
        var stdout = DrainAsync(process.StandardOutput, outputText, MaxCharactersPerStream, LimitExceeded, deadline.Token); var stderr = DrainAsync(process.StandardError, errorText, MaxCharactersPerStream, LimitExceeded, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ownGroup) KillGroup(-process.Id, 9);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } catch (IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref exceeded) != 0) throw new ProcessOutputLimitException($"{executable} exceeded the {MaxCharactersPerStream} character per-stream output limit.", outputText.ToString(), errorText.ToString());
            string Tail(StringBuilder text) { var s = text.ToString(); return s.Length <= 4096 ? s : s[^4096..]; }
            throw new ProcessTimeoutException($"{executable} exceeded {timeout.TotalSeconds:g} seconds.\nstdout: {Tail(outputText)}\nstderr: {Tail(errorText)}", outputText.ToString(), errorText.ToString());
        }
        if (Volatile.Read(ref exceeded) != 0) throw new ProcessOutputLimitException($"{executable} exceeded the {MaxCharactersPerStream} character per-stream output limit.", outputText.ToString(), errorText.ToString());
        return new(process.ExitCode, outputText.ToString(), errorText.ToString());
    }
    private static async Task DrainAsync(StreamReader reader, StringBuilder destination, int maximum, Action exceeded, CancellationToken cancellationToken)
    {
        var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            int available = maximum - destination.Length;
            destination.Append(buffer, 0, Math.Min(count, available));
            if (count > available) { exceeded(); return; }
        }
    }
}
