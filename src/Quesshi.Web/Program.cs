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
builder.Services.AddScoped<Sounds>();

// The world map is 150 KB and every map question in a duel draws the same one, so it is fetched and
// parsed once and shared. Scoped, which for a WebAssembly app is the whole session — the same
// reasoning LobbyClient's registration below gives.
builder.Services.AddScoped<WorldMapAsset>();

// One LiveClient per live duel: a page builds its own with the match's hub URL and disposes it when
// the duel is over, so this registers the factory rather than a shared instance.
builder.Services.AddTransient<Func<string, LiveClient>>(sp => hubUrl =>
{
    var appState = sp.GetRequiredService<AppState>();
    return new LiveClient(new Uri(new Uri(builder.HostEnvironment.BaseAddress), hubUrl).ToString(), () => appState.Token);
});

// One LobbyClient for the whole session, unlike LiveClient above — the app shell holds it so an
// invitation arrives wherever the player is, not just on one page — and MainLayout starts and stops
// it as the player signs in and out. Scoped, not singleton: WebAssemblyHost renders the component
// tree inside its own scope, so a singleton here would capture a second, never-initialised AppState
// and hand the hub a null token — a 401 on every negotiate. One scope per WASM app makes scoped and
// "for the whole session" the same thing anyway.
builder.Services.AddScoped(sp =>
{
    var appState = sp.GetRequiredService<AppState>();
    var hubUrl = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "hub/lobby").ToString();
    return new LobbyClient(hubUrl, () => appState.Token);
});

// The admin panel gets its own HttpClient so the two bearer tokens can never be mixed up.
builder.Services.AddScoped(sp => new AdminHttpClient(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
builder.Services.AddScoped<AdminApi>();
builder.Services.AddScoped<AdminState>();

await builder.Build().RunAsync();
