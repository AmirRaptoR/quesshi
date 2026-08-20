using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quesshi.Web;
using Quesshi.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<Translator>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<Api>();

// The admin panel gets its own HttpClient so the two bearer tokens can never be mixed up.
builder.Services.AddScoped(sp => new AdminHttpClient(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
builder.Services.AddScoped<AdminApi>();
builder.Services.AddScoped<AdminState>();

await builder.Build().RunAsync();
