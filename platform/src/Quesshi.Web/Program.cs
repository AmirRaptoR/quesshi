using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quesshi.Web;
using Quesshi.Web.Services;
using Quesshi.Web.Plugins;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IClientReloader, JsClientReloader>();
builder.Services.AddScoped<ClientHttp>();
builder.Services.AddScoped<PlayerApi>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<AppConfig>();
builder.Services.AddScoped<LanguageSelection>();
builder.Services.AddScoped<Translator>();
builder.Services.AddScoped<Sounds>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.WebAssembly.Services.LazyAssemblyLoader>();
builder.Services.AddScoped<IClientAssemblyLoader, LazyAssemblyClientLoader>();
builder.Services.AddScoped<IClientRegistry>(services =>
{
    var registry = new ClientRegistry(services.GetRequiredService<IClientAssemblyLoader>());
    new WebPluginsEntry().Register(registry);
    registry.RegisterLazy("map_pick", "Quesshi.Kinds.MapPick.Client.dll");
    return registry;
});

var host = builder.Build();
await host.Services.GetRequiredService<AppConfig>().LoadAsync();
await host.RunAsync();
