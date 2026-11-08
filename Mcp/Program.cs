using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SchematicLab.Core;
using SchematicLab.Solver;

var builder = Host.CreateApplicationBuilder(args);
// Standard output carries the protocol. Keep diagnostics on standard error.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<LessonSession>();
builder.Services.AddSingleton(new NativeRcSolver(Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH")));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<LessonTools>();
await builder.Build().RunAsync();
