using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class ClientSlotPageTests : BunitContext
{
    private const string PlayerId = "21ac7193-52fd-4d4b-9194-5cd547448072";

    [Fact]
    public void A_nav_item_contribution_appears_in_the_signed_in_shell_on_home()
    {
        var navigation = UseSignedInShell();

        var cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Equal("nav:", cut.Find("[data-test='nav-item']").TextContent));
        Assert.Equal("/", new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public void The_nav_item_contribution_remains_visible_on_a_session_path()
    {
        var navigation = UseSignedInShell();
        navigation.NavigateTo("/s/7f3c1d52-0000-4000-8000-000000000001");

        var cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Equal("nav:s/7f3c1d52-0000-4000-8000-000000000001", cut.Find("[data-test='nav-item']").TextContent));
    }

    [Fact]
    public async Task A_profile_section_contribution_appears_on_the_profile_page()
    {
        UseSignedInShell();
        await Services.GetRequiredService<AuthState>().InitializeAsync();

        var cut = Render<Profile>();

        Assert.Equal($"profile:{PlayerId}", cut.Find("[data-test='profile-section']").TextContent);
    }

    private NavigationManager UseSignedInShell()
    {
        var http = new ClientHttp(new HttpClient(new ShellHandler()) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, JSInterop.JSRuntime);
        config.LoadAsync().GetAwaiter().GetResult();
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        JSInterop.SetupVoid("quesshi.applyLanguage", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("quesshi.get", _ => true).SetResult(null);
        JSInterop.Setup<string[]>("quesshi.browserLanguages").SetResult([]);
        Services.AddSingleton(config);
        Services.AddSingleton(translator);
        Services.AddSingleton(new LanguageSelection(config, translator, JSInterop.JSRuntime));
        var api = new PlayerApi(http);
        Services.AddSingleton(api);
        Services.AddSingleton(new AuthState(api));
        Services.AddSingleton(new PlatformState(config));
        Services.AddSingleton(new StreamClient(http));
        Services.AddSingleton(new PlatformState(config));
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterSlot(ClientSlots.NavItem, typeof(NavContribution));
        registry.RegisterSlot(ClientSlots.ProfileSection, typeof(ProfileContribution));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        return Services.GetRequiredService<NavigationManager>();
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NavContribution : ComponentBase
    {
        [Parameter] public NavItemParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "span"); b.AddAttribute(1, "data-test", "nav-item"); b.AddContent(2, $"nav:{Parameters.CurrentPath}"); b.CloseElement(); }
    }

    private sealed class ProfileContribution : ComponentBase
    {
        [Parameter] public ProfileSectionParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "span"); b.AddAttribute(1, "data-test", "profile-section"); b.AddContent(2, $"profile:{Parameters.PlayerId}"); b.CloseElement(); }
    }

    private sealed class ShellHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(path switch
            {
                "/api/config" => Json("""{"name":"Tenant","languages":[{"code":"en","displayName":"English","direction":"ltr"}],"defaultLanguage":"en","capabilities":{"switch":[]},"signInMethods":[],"configVersion":1,"serverBuildId":"test"}"""),
                "/api/me" => Json($$"""{"id":"{{PlayerId}}","displayName":"North player","avatarSeed":"avatar-7","lang":"en"}"""),
                _ when path?.StartsWith("/i18n/", StringComparison.Ordinal) == true => Json("{}"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
