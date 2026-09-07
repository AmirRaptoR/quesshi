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

// One LiveClient per live duel: a page builds its own with the match's hub URL and disposes it when
// the duel is over, so this registers the factory rather than a shared instance.
builder.Services.AddTransient<Func<string, LiveClient>>(sp => hubUrl =>
{
    var appState = sp.GetRequiredService<AppState>();
    return new LiveClient(new Uri(new Uri(builder.HostEnvironment.BaseAddress), hubUrl).ToString(), () => appState.Token);
});

// One LobbyClient for the whole session, unlike LiveClient above — MainLayout starts and disposes it
// as the player signs in and out.
builder.Services.AddSingleton(sp =>
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
