using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SchematicLab.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Lesson>("#app");
builder.Services.AddScoped(_ => new RcBrowserSolver(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
await builder.Build().RunAsync();
