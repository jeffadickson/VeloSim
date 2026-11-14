using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SchematicLab.Core;
using SchematicLab.Solver;

var builder = Host.CreateApplicationBuilder(args);
// Standard output carries the protocol. Keep diagnostics on standard error.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var deployment = LessonDeployment.Parse(Environment.GetEnvironmentVariable("SCHEMATIC_LAYOUT_MODE"), Environment.GetEnvironmentVariable("SCHEMATIC_LESSON"));
builder.Services.AddSingleton(deployment.CreateSession());
var solverPath = Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH");
// The standalone distribution can place both hosts beside the same optional
// solver. Explicit configuration keeps precedence over the installed default.
if (string.IsNullOrWhiteSpace(solverPath))
{
    solverPath = SolverLocation.FindPackaged(AppContext.BaseDirectory);
}
builder.Services.AddSingleton(new NativeRcSolver(solverPath));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<LessonTools>();
await builder.Build().RunAsync();
