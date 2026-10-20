using System.Globalization;
using System.Text;

namespace VeloSim;

public static class SchedulerSystemVerilog
{
    public static string Expression(SchedulerProcess process)
    {
        string S(int id) => "s" + id.ToString(CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(process.Operation) || process.A < 0 || process.B < 0) throw new ArgumentException("Unsupported scheduler expression.");
        return process.Operation switch
        {
            ScalarOperation.Copy => S(process.A), ScalarOperation.And => S(process.A) + " & " + S(process.B), ScalarOperation.Or => S(process.A) + " | " + S(process.B), ScalarOperation.Xor => S(process.A) + " ^ " + S(process.B), ScalarOperation.Not => "~" + S(process.A), _ => throw new ArgumentException("Unsupported scheduler expression.")
        };
    }
    public static void Validate(SchedulerFixture fixture)
    {
        if (fixture.SignalCount > 4096 || fixture.Processes.Count > 4096 || fixture.Drives.Count > 100_000 || fixture.Drives.Sum(d => (long)d.Count) > 1_000_000) throw new ArgumentException("Fixture exceeds SystemVerilog differential resource limits.");
        var clocks = fixture.Processes.Select(p => p.Clock).ToHashSet();
        if (fixture.Processes.Any(p => clocks.Contains(p.Destination))) throw new ArgumentException("Generated-clock feedback is outside the single-batch scheduler contract.");
    }
    public static string Emit(SchedulerFixture fixture)
    {
        Validate(fixture); var source = new StringBuilder("module velosim_scheduler_tb;\nreg ready=0;\ninteger active=0;\n");
        for (int id = 0; id < fixture.SignalCount; id++) source.Append("reg s").Append(id.ToString(CultureInfo.InvariantCulture)).Append(";\n");
        foreach (var process in fixture.Processes)
            source.Append("always @(").Append(process.Edge == IrEdge.Posedge ? "posedge" : "negedge").Append(" s").Append(process.Clock.ToString(CultureInfo.InvariantCulture)).Append(") if (ready) begin active=active+1; s").Append(process.Destination.ToString(CultureInfo.InvariantCulture)).Append(" <= ").Append(Expression(process)).Append("; end\n");
        source.Append("initial begin\n#1; ready=1;\n$display(\"VS_SCHED_HEADER ").Append(fixture.SignalCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(fixture.Processes.Count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(fixture.Observed.Count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(fixture.Drives.Count.ToString(CultureInfo.InvariantCulture)).Append("\");\n");
        for (int step = 0; step < fixture.Drives.Count; step++)
        {
            source.Append("active=0;\n");
            // Blocking assignments within this uninterrupted active-region thread expose one complete precommit snapshot to always blocks.
            foreach (var (signal, value) in fixture.Drives[step]) source.Append('s').Append(signal.ToString(CultureInfo.InvariantCulture)).Append("=1'b").Append(ScalarLogic.Format(value)).Append(";\n");
            source.Append("#1; $display(\"VS_SCHED ").Append(step.ToString(CultureInfo.InvariantCulture)).Append(' ').Append((step + 1).ToString(CultureInfo.InvariantCulture)).Append(" %0d %").Append(fixture.Observed.Count.ToString(CultureInfo.InvariantCulture)).Append("b\",active,{").Append(string.Join(',', fixture.Observed.Select(id => "s" + id.ToString(CultureInfo.InvariantCulture)))).Append("});\n");
        }
        return source.Append("$finish;\nend\nendmodule\n").ToString();
    }
}
