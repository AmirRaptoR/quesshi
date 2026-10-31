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
builder.Services.AddScoped<LobbyActions>();
builder.Services.AddScoped<StreamClient>(services => new StreamClient(services.GetRequiredService<ClientHttp>()));
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<AppConfig>();
builder.Services.AddScoped<PlatformState>(services => new PlatformState(
    services.GetRequiredService<AppConfig>(), services.GetRequiredService<StreamClient>()));
builder.Services.AddScoped<LanguageSelection>();
builder.Services.AddScoped<Translator>();
builder.Services.AddScoped<IClientTranslator>(services => services.GetRequiredService<Translator>());
builder.Services.AddScoped<Sounds>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.WebAssembly.Services.LazyAssemblyLoader>();
builder.Services.AddScoped<IClientAssemblyLoader, LazyAssemblyClientLoader>();
builder.Services.AddScoped<IAdminClientRegistry>(services => new AdminClientRegistry(
    services.GetRequiredService<IClientAssemblyLoader>(),
    services.GetRequiredService<IClientTranslationRegistry>()));
builder.Services.AddScoped<IClientTranslationRegistry>(services => services.GetRequiredService<Translator>());
builder.Services.AddScoped<IClientRegistry>(services =>
{
    var registry = new ClientRegistry(services.GetRequiredService<IClientAssemblyLoader>());
    new WebPluginsEntry().Register(registry);
    registry.RegisterLazy("map_pick", "Quesshi.Kinds.MapPick.Client.dll");
    return registry;
});

builder.Services.AddScoped<IFormatClientRegistry>(services => (IFormatClientRegistry)services.GetRequiredService<IClientRegistry>());

var host = builder.Build();
await host.Services.GetRequiredService<AppConfig>().LoadAsync();
_ = host.Services.GetRequiredService<PlatformState>();
await host.RunAsync();
