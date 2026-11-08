using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SchematicLab.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Lesson>("#app");
await builder.Build().RunAsync();
