using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using SchematicLab.Core;
using SchematicLab.Solver;
using SchematicLab.WebHost;
using System.Text.Json;
using System.Text.Json.Serialization;

var command = StartupOptions.Parse(args, AppContext.BaseDirectory,
    Environment.GetEnvironmentVariable("SCHEMATIC_WEB_ROOT"), Environment.GetEnvironmentVariable("SPICE_SOLVER_PATH"));
return command switch
{
    ShowSchematicHelp => Help(),
    InvalidSchematicStartup failure => Refuse(failure),
    ServeSchematic serve => await ServeAsync(serve.Options),
};

static int Help()
{
    Console.WriteLine("SchematicLab [--port 5087] [--web-root ABSOLUTE_PATH] [--solver ABSOLUTE_PATH]");
    Console.WriteLine("Local browser schematic. Default assets: installed wwwroot; optional solver: installed solver directory.");
    return 0;
}

static int Refuse(InvalidSchematicStartup failure)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(failure, StartupJsonContext.Default.InvalidSchematicStartup));
    return 64;
}

static async Task<int> ServeAsync(SchematicHostOptions configuration)
{
LessonDeployment deployment;
try { deployment = LessonDeployment.Parse(Environment.GetEnvironmentVariable("SCHEMATIC_LAYOUT_MODE"), Environment.GetEnvironmentVariable("SCHEMATIC_LESSON")); }
catch (ArgumentException error) { return Refuse(new("invalid_lesson_deployment", error.Message)); }
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.ConfigureKestrel(options =>
{
    // Discard configuration-defined listeners: deployment environment must not
    // add a public endpoint alongside the explicitly local application listener.
    options.Configure(new ConfigurationBuilder().Build());
    options.ListenLocalhost(configuration.Port);
    options.Limits.MaxRequestBodySize = 64 * 1024;
});
builder.Services.AddSingleton(new NativeRcSolver(configuration.SolverPath));
var app = builder.Build();
var files = new PhysicalFileProvider(configuration.WebRoot);
app.Lifetime.ApplicationStopped.Register(files.Dispose);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
var contentTypes = new FileExtensionContentTypeProvider();
// Published Blazor includes fingerprinted ICU data files.
contentTypes.Mappings[".dat"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = contentTypes });
app.MapGet("/api/lesson-deployment", () => deployment);
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
return 0;
}

[JsonSerializable(typeof(InvalidSchematicStartup))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class StartupJsonContext : JsonSerializerContext;
