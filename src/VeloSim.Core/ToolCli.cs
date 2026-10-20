using System.Globalization;

namespace VeloSim;

public static class ToolCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner = null, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] != "compile") return Run(args, output, error);
        try
        {
            string? top = null, directory = null, surelog = null, normalizer = null; int workers = 1;
            var sources = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--") { sources.AddRange(args.Skip(i + 1)); break; }
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { sources.Add(args[i]); continue; }
                var option = args[i];
                if (option is not ("--top" or "--output-dir" or "--surelog" or "--normalizer" or "--threads") || ++i == args.Length) throw new ArgumentException("Unknown or incomplete compile option: " + option);
                switch (option)
                {
                    case "--top": top = args[i]; break;
                    case "--output-dir": directory = args[i]; break;
                    case "--surelog": surelog = args[i]; break;
                    case "--normalizer": normalizer = args[i]; break;
                    case "--threads": if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out workers)) throw new ArgumentException("Invalid thread count."); break;
                }
            }
            if (top is null || directory is null) throw new ArgumentException("Compile requires --top and --output-dir.");
            output.WriteLine(await new DesignPipeline(runner).CompileAsync(sources, top, directory, surelog, normalizer, workers, cancellationToken: cancellationToken).ConfigureAwait(false));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { error.WriteLine(ex.Message); return 1; }
    }
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length < 2 || args[0] != "simulate") throw new ArgumentException("Usage: velosim simulate <ir.json> [--cycles N] [--threads N] [--release-reset]");
            int cycles = 5, workers = 1; bool release = false;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--release-reset") { release = true; continue; }
                if (args[i] is not ("--cycles" or "--threads") || i + 1 == args.Length) throw new ArgumentException("Unknown or incomplete option: " + args[i]);
                var option = args[i++]; if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) throw new ArgumentException("Expected positive integer.");
                if (option == "--cycles") cycles = n; else workers = n;
            }
            if (cycles is < 1 or > 10_000_000 || workers is < 1 or > 256) throw new ArgumentException("Cycles must be 1..10000000 and threads 1..256.");
            var design = IrDesign.Parse(File.ReadAllText(args[1]));
            if (!design.Signals.Any(s => s.Name == "clk" && s.Initial.Width == 1)) throw new ArgumentException("Simulation command requires scalar clk.");
            if (release && !design.Signals.Any(s => s.Name == "rst_n" && s.Initial.Width == 1)) throw new ArgumentException("Release reset requires scalar rst_n.");
            using var sim = new Simulation(design);
            if (release) sim.Drive(new Dictionary<string, FourStateWord> { ["rst_n"] = new(1) }, workers);
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                if (release && cycle == 1) sim.Drive(new Dictionary<string, FourStateWord> { ["rst_n"] = new(1, 1) }, workers);
                sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1) }, workers);
                sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }, workers);
                output.Write("cycle " + (cycle + 1).ToString(CultureInfo.InvariantCulture));
                foreach (var s in design.Signals.Where(s => s.Name != "clk")) { var word = sim.Read(s.Name); output.Write($" {s.Name}=({word.Width},{word.Value},{word.XMask},{word.ZMask})"); }
                output.WriteLine();
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { error.WriteLine(ex.Message); return 1; }
    }
}
