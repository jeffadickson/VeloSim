using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SchematicLab.Web;
using SchematicLab.Core;
using System.Net.Http.Json;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Lesson>("#app");
// A missing or malformed bootstrap must stop startup rather than silently turn
// an author's locked lesson into an editable document.
using var bootstrap = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var deployment = await bootstrap.GetFromJsonAsync<LessonDeployment>("api/lesson-deployment")
    ?? throw new InvalidOperationException("Lesson deployment is missing.");
builder.Services.AddSingleton(deployment.CreateSession());
builder.Services.AddScoped(_ => new RcBrowserSolver(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
await builder.Build().RunAsync();
