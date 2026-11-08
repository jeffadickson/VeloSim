using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using SchematicLab.Core;
using SchematicLab.Solver;

var builder = WebApplication.CreateBuilder(args);
var path = Environment.GetEnvironmentVariable("SCHEMATIC_WEB_ROOT");
if (path is null || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
    throw new InvalidOperationException("Set SCHEMATIC_WEB_ROOT to the published browser wwwroot directory.");

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5087);
    options.Limits.MaxRequestBodySize = 64 * 1024;
});
builder.Services.AddSingleton(new NativeRcSolver(Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH")));
var app = builder.Build();
var files = new PhysicalFileProvider(path);
app.Lifetime.ApplicationStopped.Register(files.Dispose);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
var contentTypes = new FileExtensionContentTypeProvider();
// Published Blazor includes fingerprinted ICU data files.
contentTypes.Mappings[".dat"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = contentTypes });
app.MapPost("/api/rc", async (RcInput input, NativeRcSolver solver, CancellationToken cancellation) =>
{
    try { return Results.Ok(await solver.SolveAsync(input, cancellation)); }
    catch (RcSolverException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapPost("/api/circuit", async (CircuitJob job, NativeRcSolver solver, CancellationToken cancellation) =>
{
    try { return Results.Ok(await solver.SolveCircuitAsync(job, cancellation)); }
    catch (RcSolverException error) { return Results.BadRequest(new { error = error.Message }); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid numeric circuit request." }); }
});
await app.RunAsync();
