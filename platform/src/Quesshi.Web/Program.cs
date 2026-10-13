using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quesshi.Web;
using Quesshi.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IClientReloader, JsClientReloader>();
builder.Services.AddScoped<ClientHttp>();
builder.Services.AddScoped<AppConfig>();
builder.Services.AddScoped<LanguageSelection>();
builder.Services.AddScoped<Translator>();
builder.Services.AddScoped<Sounds>();

var host = builder.Build();
await host.Services.GetRequiredService<AppConfig>().LoadAsync();
await host.RunAsync();
