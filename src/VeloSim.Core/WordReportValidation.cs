using System.Globalization;

namespace VeloSim;

public static class WordReportValidation
{
    public static void Validate(WordDifferentialReport report)
    {
        WordSystemVerilog.Validate(report.Fixture);
        void Trace(IReadOnlyList<WordHdlStep> trace)
        {
            if (trace.Count != report.Fixture.Drives.Count) throw new IrValidationException("Word report trace step count mismatch.");
            for (int i = 0; i < trace.Count; i++)
            {
                var row = trace[i];
                if (row.Tick != (ulong)i + 1 || row.ActivatedProcesses < 0 || row.ActivatedProcesses > report.Fixture.Design.Processes.Count || row.Values.Count != report.Fixture.Design.Signals.Count) throw new IrValidationException("Invalid typed word trace dimensions.");
                for (int signal = 0; signal < row.Values.Count; signal++) if (row.Values[signal] is not { } value || value.Length != report.Fixture.Design.Signals[signal].Initial.Width || value.Any(c => c is not ('0' or '1' or 'x' or 'z'))) throw new IrValidationException("Invalid typed word trace bits.");
            }
        }
        Trace(report.Oracle); if (report.Reference is not null) Trace(report.Reference);
        foreach (var (worker, trace) in report.Native) { if (!report.Fixture.Workers.Contains(worker)) throw new IrValidationException("Unexpected native word worker."); Trace(trace); }
        foreach (var (key, value) in report.Errors)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new IrValidationException("Empty word failure evidence.");
            if (key == "icarus") { if (report.Reference is not null) throw new IrValidationException("Word reference has success and failure evidence."); }
            else if (key is not ("canceled" or "native_metadata") && (!key.StartsWith("native-", StringComparison.Ordinal) || !int.TryParse(key.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var worker) || key != "native-" + worker.ToString(CultureInfo.InvariantCulture) || !report.Fixture.Workers.Contains(worker) || report.Native.ContainsKey(worker))) throw new IrValidationException("Invalid typed word engine failure.");
        }
    }
}
