using System.Globalization;

namespace VeloSim;

public static class CounterTestbench
{
    public static string Generate(string designTop, int width, IReadOnlyList<int> resets)
    {
        CounterStimulus.Validate(resets);
        if (width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(width));
        if (string.IsNullOrEmpty(designTop) || !(char.IsAsciiLetter(designTop[0]) || designTop[0] == '_') || designTop.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '$'))) throw new ArgumentException("Top must be a simple SystemVerilog identifier.");
        // Named ports deliberately bind the same RTL DUT compiled by Surelog.
        var lines = new List<string> { "module velosim_reference_tb;", "reg clk=0; reg rst_n=0;", $"wire [{width - 1}:0] count;", $"{designTop} dut(.clk(clk),.rst_n(rst_n),.count(count));", "initial begin", $"if ($bits(dut.count)!={width} || $bits(dut.clk)!=1 || $bits(dut.rst_n)!=1) $fatal(1,\"Counter port contract mismatch\");", "$display(\"VS_WIDTH %0d\",$bits(dut.count));" };
        for (int i = 0; i < resets.Count; i++) lines.Add($"rst_n=1'b{resets[i]}; #1 clk=1; #1 $display(\"VS_RESULT {i.ToString(CultureInfo.InvariantCulture)} %{width.ToString(CultureInfo.InvariantCulture)}b\",count); #1 clk=0;");
        lines.AddRange(["$finish;", "end", "endmodule"]); return string.Join('\n', lines) + "\n";
    }
}
