namespace SchematicLab.Core;

/// <summary>Bounded charging experiment, in ohms, farads, volts and seconds.</summary>
public sealed record RcInput(double Ohms, double Farads, double Volts)
{
    public double RiseSeconds => TimeConstant / 100;
    public double TimeConstant => Ohms * Farads;
    public double StopSeconds => RiseSeconds + 5 * TimeConstant;
    public bool IsValid => double.IsFinite(Ohms) && Ohms > 0 && double.IsFinite(Farads) && Farads > 0 &&
        Volts is > 0 and <= 100 && TimeConstant is >= 1e-5 and <= 1;
}
public sealed record LoadRc(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record RunTransient(DocumentVersion Expected, string ProbeId) : DocumentCommand(Expected);
public sealed record TransientSample(double Seconds, double Volts);
public sealed record TransientRun(Guid Id, DocumentVersion Version, string ProbeId,
    RcInput Input, IReadOnlyList<TransientSample> Samples);

/// <summary>Expected transport or solver failure without a completed waveform.</summary>
public sealed class RcSolverException(string message) : Exception(message);
