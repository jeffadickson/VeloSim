using System.Globalization;
using System.Text;

namespace VeloSim;

public static class WordSystemVerilog
{
    public static void Validate(WordFixture fixture)
    {
        ConformanceLimits.Default.Validate(fixture.Design, fixture.Drives, fixture.Workers);
        if (fixture.Design.Signals.Count > 4096 || fixture.Design.Processes.Count > 4096 || fixture.Drives.Count > 100_000 || fixture.Drives.Sum(d => (long)d.Count) > 1_000_000 || fixture.Design.Signals.Sum(s => (long)s.Initial.Width) * fixture.Drives.Count * fixture.Workers.Count > 10_000_000) throw new ArgumentException("Word HDL fixture exceeds differential resource limits.");
        if (fixture.Design.Processes.Any(p => p.Operation is IrOperation.Increment or IrOperation.ResetIncrement or IrOperation.SvIfResetIncrement)) throw new ArgumentException("Experimental bit-precise increment/reset operations are not strict SystemVerilog arithmetic and cannot enter HDL conformance.");
        var clocks = fixture.Design.Processes.Select(p => p.Clock).ToHashSet(StringComparer.Ordinal);
        if (fixture.Design.Processes.Any(p => clocks.Contains(p.Destination))) throw new ArgumentException("Computed-clock feedback is outside the atomic single-batch word contract.");
    }
    public static string ProcessBody(IrDesign design, int index)
    {
        var ids = design.Signals.Select((s, i) => (s.Name, Id: i)).ToDictionary(s => s.Name, s => s.Id, StringComparer.Ordinal); var widths = design.Signals.ToDictionary(s => s.Name, s => s.Initial.Width, StringComparer.Ordinal);
        var p = design.Processes[index]; var width = widths[p.Destination];
        bool bitwise = p.Operation is IrOperation.And or IrOperation.Or or IrOperation.Xor or IrOperation.Not;
        var calculationWidth = bitwise ? width : Math.Max(width, Math.Max(widths[p.Source], widths[p.SourceB ?? p.Source]));
        string N(int n) => n.ToString(CultureInfo.InvariantCulture); string S(string name) => "s" + N(ids[name]); var a = "a" + N(index); var b = "b" + N(index);
        var expression = p.Operation switch
        {
            IrOperation.Copy => S(p.Source),
            IrOperation.SvIncrement or IrOperation.SvIfResetIncrementStrict => a + " + " + N(calculationWidth) + "'d1",
            IrOperation.And => a + " & " + b, IrOperation.Or => a + " | " + b, IrOperation.Xor => a + " ^ " + b, IrOperation.Not => "~" + a,
            IrOperation.UnsignedAdd => a + " + " + b, IrOperation.UnsignedSubtract => a + " - " + b, IrOperation.UnsignedMultiply => a + " * " + b,
            _ => throw new ArgumentException("Unsupported strict word operation.")
        };
        var result = new StringBuilder("reg [").Append(N(calculationWidth - 1)).Append(":0] ").Append(a).Append(',').Append(b).Append(";\nalways @(").Append(p.Edge == IrEdge.Posedge ? "posedge " : "negedge ").Append(S(p.Clock)).Append(") if (ready) begin\nactive=active+1;\n");
        // Procedural temporary assignments avoid continuous-assignment delta-cycle races with atomic input drives.
        result.Append(a).Append('=').Append(S(p.Source)).Append(';').Append(b).Append('=').Append(S(p.SourceB ?? p.Source)).Append(";\n");
        if (p.Operation == IrOperation.SvIfResetIncrementStrict) result.Append("if (!").Append(S(p.Reset)).Append(") ").Append(S(p.Destination)).Append(" <= ").Append(N(width)).Append("'d0; else ");
        result.Append(S(p.Destination)).Append(" <= ").Append(expression).Append(";\nend\n"); return result.ToString();
    }
    public static string Emit(WordFixture fixture)
    {
        Validate(fixture); string N(int n) => n.ToString(CultureInfo.InvariantCulture); var ids = fixture.Design.Signals.Select((s, i) => (s.Name, Id: i)).ToDictionary(s => s.Name, s => s.Id, StringComparer.Ordinal);
        var source = new StringBuilder("module velosim_word_tb;\nreg ready=0; integer active=0;\n");
        foreach (var (s, index) in fixture.Design.Signals.Select((s, i) => (s, i))) source.Append("reg [").Append(N(s.Initial.Width - 1)).Append(":0] s").Append(N(index)).Append(";\n");
        for (int i = 0; i < fixture.Design.Processes.Count; i++) source.Append(ProcessBody(fixture.Design, i));
        source.Append("initial begin\n");
        foreach (var (s, index) in fixture.Design.Signals.Select((s, i) => (s, i))) source.Append('s').Append(N(index)).Append('=').Append(N(s.Initial.Width)).Append("'b").Append(FourStateCodec.FormatBinary(s.Initial)).Append(";\n");
        source.Append("#1; ready=1;\n$display(\"VS_WORD_HEADER ").Append(N(fixture.Design.Signals.Count)).Append(' ').Append(N(fixture.Design.Processes.Count)).Append(' ').Append(N(fixture.Drives.Count)).Append("\");\n");
        for (int i = 0; i < fixture.Design.Signals.Count; i++) source.Append("$display(\"VS_WORD_SIGNAL ").Append(N(i)).Append(' ').Append(N(fixture.Design.Signals[i].Initial.Width)).Append("\");\n");
        for (int step = 0; step < fixture.Drives.Count; step++)
        {
            source.Append("active=0;\n");
            foreach (var (name, word) in fixture.Drives[step]) source.Append('s').Append(N(ids[name])).Append('=').Append(N(word.Width)).Append("'b").Append(FourStateCodec.FormatBinary(word)).Append(";\n");
            source.Append("#1; $display(\"VS_WORD_STEP ").Append(N(step)).Append(' ').Append(N(step + 1)).Append(" %0d\",active);\n");
            for (int i = 0; i < fixture.Design.Signals.Count; i++) source.Append("$display(\"VS_WORD_VALUE ").Append(N(step)).Append(' ').Append(N(i)).Append(" %").Append(N(fixture.Design.Signals[i].Initial.Width)).Append("b\",s").Append(N(i)).Append(");\n");
        }
        return source.Append("$finish;\nend\nendmodule\n").ToString();
    }
}
