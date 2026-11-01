using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AdminFeaturePageTests : BunitContext
{
    private static readonly IReadOnlySet<string> EditorRoles = new HashSet<string>(["Owner", "Editor"], StringComparer.OrdinalIgnoreCase);
    private readonly AdminClientRegistry registry = new(new EmptyLoader(), new EmptyTranslationRegistry());

    public AdminFeaturePageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
        Services.AddSingleton<IAdminClientRegistry>(registry);
    }

    [Fact]
    public async Task Feature_page_navigation_appears_for_a_permitted_role()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", "reports.nav", typeof(ReportsPage), EditorRoles));
        var state = await StateAsync("Editor");
        var cut = Render<AdminNav>(p => p.AddCascadingValue(state));

        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/reports");
    }

    [Fact]
    public async Task Feature_page_navigation_is_absent_for_a_lower_role()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", "reports.nav", typeof(ReportsPage), EditorRoles));
        var state = await StateAsync("Moderator");
        var cut = Render<AdminNav>(p => p.AddCascadingValue(state));

        Assert.DoesNotContain(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/reports");
    }

    [Fact]
    public async Task Feature_page_navigation_is_absent_when_the_tenant_capability_is_off()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", "reports.nav", typeof(ReportsPage), EditorRoles, new AdminCapability("feature", "reports")));
        var state = await StateAsync("Owner", """{"capabilities":{"feature":["stats"]}}""");
        var cut = Render<AdminNav>(p => p.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("a").Count > 0);
        Assert.DoesNotContain(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/reports");
    }

    [Fact]
    public async Task Feature_page_navigation_appears_when_the_tenant_capability_is_on()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", "reports.nav", typeof(ReportsPage), EditorRoles, new AdminCapability("feature", "reports")));
        var state = await StateAsync("Owner", """{"capabilities":{"feature":["reports"]}}""");
        var cut = Render<AdminNav>(p => p.AddCascadingValue(state));

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/reports"));
    }

    [Fact]
    public async Task Permitted_admin_opens_the_feature_page_through_the_route_host()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", "reports.nav", typeof(ReportsPage), EditorRoles, new AdminCapability("feature", "reports")));
        var state = await StateAsync("Editor", """{"capabilities":{"feature":["reports"]}}""");

        var cut = Render<AdminFeaturePage>(p => p.AddCascadingValue(state).Add(page => page.Path, "reports"));

        cut.WaitForAssertion(() => Assert.Contains("Reports content", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lower_role_cannot_open_the_feature_page_through_the_route_host()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("reports", "reports.nav", typeof(ReportsPage), EditorRoles));
        var state = await StateAsync("Moderator");

        var cut = Render<AdminFeaturePage>(p => p.AddCascadingValue(state).Add(page => page.Path, "reports"));

        Assert.DoesNotContain("Reports content", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=admin-feature-denied]"));
    }

    [Fact]
    public async Task Throwing_feature_page_leaves_the_admin_shell_usable()
    {
        registry.RegisterFeaturePage(new AdminPageContribution("broken", "broken.nav", typeof(ThrowingPage), EditorRoles));
        var state = await StateAsync("Editor");
        RenderFragment body = builder =>
        {
            builder.OpenComponent<AdminFeaturePage>(0);
            builder.AddAttribute(1, nameof(AdminFeaturePage.Path), "broken");
            builder.CloseComponent();
        };

        var cut = Render<AdminLayout>(p => p.AddCascadingValue(state).Add(layout => layout.Body, body));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=admin-feature-failed]")));
        Assert.NotEmpty(cut.FindAll("nav.admin-nav"));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/broken");
    }

    private async Task<AdminState> StateAsync(string role, string config = "{}")
    {
        var handler = new Handler(Response($$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}"""), config);
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private static string Response(string json) => json;

    private sealed class ReportsPage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "Reports content");
    }

    private sealed class ThrowingPage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("boom");
    }

    private sealed class Handler(string session, string config) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath == "/api/config" ? config : session;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class EmptyLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }

    private sealed class EmptyTranslationRegistry : IClientTranslationRegistry
    {
        public void Register(ClientTranslation translation) { }
    }

    private sealed class Translator : IClientTranslator
    {
        public string this[string key] => key;
    }
}
