using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AdminPageTests : BunitContext
{
    [Fact]
    public async Task Temporary_password_admin_is_sent_to_password_change_before_the_shell()
    {
        var state = await StateAsync(Response(HttpStatusCode.OK, SessionJson(mustChangePassword: true)));
        var navigation = Services.GetRequiredService<NavigationManager>();

        Render<AdminHome>(parameters => parameters.AddCascadingValue(state));

        Assert.EndsWith("/admin/password", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_admin_login_shows_the_localized_server_error_without_the_shell()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Unauthorized, """{"code":"admin.auth.invalid"}"""));
        var cut = Render<AdminLogin>(parameters => parameters.AddCascadingValue(state));

        cut.FindAll("input")[0].Change("ada");
        cut.FindAll("input")[1].Change("wrong");
        cut.Find("form").Submit();

        Assert.DoesNotContain("Tenant administration", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Invalid admin credentials.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locked_admin_login_shows_the_localized_server_error_without_the_shell()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.TooManyRequests, """{"code":"admin.auth.locked"}"""));
        var cut = Render<AdminLogin>(parameters => parameters.AddCascadingValue(state));

        cut.FindAll("input")[0].Change("ada");
        cut.FindAll("input")[1].Change("wrong");
        cut.Find("form").Submit();

        Assert.Contains("This account is temporarily locked.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_accounts_offer_membership_controls_only_for_other_ordinary_admins()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, """
                [
                  {"id":"21ac7193-52fd-4d4b-9194-5cd547448072","username":"Ada","email":"ada@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Owner"},
                  {"id":"77bc7193-52fd-4d4b-9194-5cd547448072","username":"Grace","email":"grace@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Admin"},
                  {"id":"88bc7193-52fd-4d4b-9194-5cd547448072","username":"Lin","email":"lin@example.test","isActive":true,"isMembershipActive":false,"isPlatform":false,"mustChangePassword":false,"role":"Owner"}
                ]
                """));

        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article button")));

        Assert.Contains("Grace", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_reset_link_error_is_rendered_with_the_server_localization_key()
    {
        var cut = await ResetErrorAsync("admin.auth.linkExpired");
        Assert.Contains("This reset link has expired.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Used_reset_link_error_is_rendered_with_the_server_localization_key()
    {
        var cut = await ResetErrorAsync("admin.auth.linkUsed");
        Assert.Contains("This reset link has already been used.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_reset_link_error_is_rendered_with_the_server_localization_key()
    {
        var cut = await ResetErrorAsync("admin.auth.linkInvalid");
        Assert.Contains("This reset link is not valid.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_out_clears_the_admin_session_and_refreshes_its_antiforgery_token()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.OK, SessionJson()),
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, """{"token":"after"}"""));

        await state.SignOutAsync();

        Assert.Equal(AdminSessionStatus.SignedOut, state.Status);
    }

    [Fact]
    public async Task Failed_sign_out_keeps_the_admin_session_available()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.OK, SessionJson()),
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.InternalServerError, """{"code":"admin.error"}"""),
            Response(HttpStatusCode.OK, """{"token":"after"}"""));

        await state.SignOutAsync();

        Assert.Equal(AdminSessionStatus.SignedIn, state.Status);
    }

    private async Task<AdminState> StateAsync(params HttpResponseMessage[] responses)
    {
        Services.AddSingleton<IClientTranslator>(new TestTranslator());
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(new Handler(responses))
        {
            BaseAddress = new Uri("https://tenant.example/")
        }, new NullReloader())));
        await state.InitializeAsync();
        return state;
    }

    private async Task<IRenderedComponent<AdminReset>> ResetErrorAsync(string error)
    {
        var state = await StateAsync(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest, $$"""{"code":"{{error}}"}"""));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/reset?token=expired");
        var cut = Render<AdminReset>(parameters => parameters.AddCascadingValue(state));
        cut.Find("input").Change("new-password");
        cut.Find("form").Submit();
        await cut.InvokeAsync(() => Task.CompletedTask);
        return cut;
    }

    private static string SessionJson(string role = "Admin", bool mustChangePassword = false) =>
        $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":{{mustChangePassword.ToString().ToLowerInvariant()}}}""";

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
    {
        Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class TestTranslator : IClientTranslator
    {
        private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
        {
            ["common.loading"] = "Loading",
            ["admin.home.title"] = "Administration",
            ["admin.home.body"] = "Manage this tenant.",
            ["admin.login.title"] = "Admin sign-in",
            ["admin.login.submit"] = "Sign in",
            ["admin.login.forgot"] = "Forgot password?",
            ["admin.field.username"] = "Username",
            ["admin.field.password"] = "Password",
            ["admin.auth.invalid"] = "Invalid admin credentials.",
            ["admin.auth.locked"] = "This account is temporarily locked.",
            ["admin.reset.title"] = "Set a new password",
            ["admin.reset.submit"] = "Set password",
            ["admin.auth.linkExpired"] = "This reset link has expired.",
            ["admin.auth.linkUsed"] = "This reset link has already been used.",
            ["admin.auth.linkInvalid"] = "This reset link is not valid.",
            ["admin.accounts.title"] = "Admin accounts",
            ["admin.accounts.create"] = "Create admin",
            ["admin.field.email"] = "Email",
            ["admin.accounts.active"] = "Active",
            ["admin.accounts.inactive"] = "Inactive",
            ["admin.accounts.deactivate"] = "Deactivate",
            ["admin.accounts.reactivate"] = "Reactivate",
            ["admin.accounts.denied"] = "You cannot manage accounts.",
        };

        public string this[string key] => Values.TryGetValue(key, out var value) ? value : key;
    }
}
