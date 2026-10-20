using VeloSim;
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return BenchmarkCli.Run(args, Console.Out, Console.Error, cancellation.Token);
