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
    public AdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new TestTranslator());
    }

    [Fact]
    public async Task Login_sends_a_temporary_password_admin_to_password_change_before_the_shell()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, """{"mustChangePassword":true}"""),
            Response(HttpStatusCode.OK, """{"token":"after-login"}"""),
            Response(HttpStatusCode.OK, SessionJson(mustChangePassword: true)));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<AdminLogin>(parameters => parameters.AddCascadingValue(state));

        cut.FindAll("input")[0].Change("ada");
        cut.FindAll("input")[1].Change("A strong password 2026!");
        cut.Find("form").Submit();

        Assert.EndsWith("/admin/password", navigation.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-shell", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_sends_an_eligible_admin_to_the_admin_home()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, """{"mustChangePassword":false}"""),
            Response(HttpStatusCode.OK, """{"token":"after-login"}"""),
            Response(HttpStatusCode.OK, SessionJson()));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<AdminLogin>(parameters => parameters.AddCascadingValue(state));

        cut.FindAll("input")[0].Change("ada");
        cut.FindAll("input")[1].Change("A strong password 2026!");
        cut.Find("form").Submit();

        Assert.EndsWith("/admin", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_temporary_password_admin_cannot_open_accounts_before_changing_the_password()
    {
        var state = await StateAsync(Response(HttpStatusCode.OK, SessionJson(mustChangePassword: true)));
        var navigation = Services.GetRequiredService<NavigationManager>();

        Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));

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
        Assert.Equal(AdminSessionStatus.SignedOut, state.Status);
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
    public async Task Owner_accounts_show_membership_state_and_controls_only_for_other_ordinary_admins()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, """
                [
                  {"id":"21ac7193-52fd-4d4b-9194-5cd547448072","username":"Ada","email":"ada@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Owner"},
                  {"id":"77bc7193-52fd-4d4b-9194-5cd547448072","username":"Grace","email":"grace@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Admin"},
                  {"id":"88bc7193-52fd-4d4b-9194-5cd547448072","username":"Lin","email":"lin@example.test","isActive":true,"isMembershipActive":false,"isPlatform":false,"mustChangePassword":false,"role":"Admin"},
                  {"id":"99bc7193-52fd-4d4b-9194-5cd547448072","username":"Platform","email":"platform@example.test","isActive":true,"isMembershipActive":true,"isPlatform":true,"mustChangePassword":false,"role":"Admin"}
                ]
                """));

        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("article button").Count));

        Assert.Contains("Active", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Inactive", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Deactivate", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Reactivate", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deactivating_an_ordinary_admin_posts_the_tenant_membership_change_and_reloads_accounts()
    {
        var adminId = Guid.Parse("77bc7193-52fd-4d4b-9194-5cd547448072");
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, AccountsJson(active: true)),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, AccountsJson(active: false)));
        var state = await StateAsync(handler);
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article button")));

        cut.Find("article button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Inactive", cut.Markup, StringComparison.Ordinal));

        Assert.Contains($"/api/admin/accounts/{adminId}/membership/active?value=false", handler.Paths);
    }

    [Fact]
    public async Task Reactivating_an_ordinary_admin_posts_the_tenant_membership_change_and_reloads_accounts()
    {
        var adminId = Guid.Parse("77bc7193-52fd-4d4b-9194-5cd547448072");
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, AccountsJson(active: false)),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, AccountsJson(active: true)));
        var state = await StateAsync(handler);
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article button")));

        cut.Find("article button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Active", cut.Markup, StringComparison.Ordinal));

        Assert.Contains($"/api/admin/accounts/{adminId}/membership/active?value=true", handler.Paths);
    }

    [Fact]
    public async Task Creating_an_admin_reloads_the_account_list()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, "[]"),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, AccountJson("New admin")),
            Response(HttpStatusCode.OK, AccountJson("New admin", array: true)));
        var state = await StateAsync(handler);
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("form")));

        cut.FindAll("input")[0].Change("newadmin");
        cut.FindAll("input")[1].Change("newadmin@example.test");
        cut.FindAll("input")[2].Change("A strong password 2026!");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("New admin", cut.Markup, StringComparison.Ordinal));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/admin/accounts");
    }

    [Fact]
    public async Task Creating_an_admin_shows_a_localized_failure()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, "[]"),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest, """{"code":"admin.auth.badUsername"}"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("form")));

        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Enter a valid username.", cut.Markup, StringComparison.Ordinal));

        Assert.DoesNotContain("admin.auth.badUsername", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Account_management_failures_use_localized_errors()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, AccountsJson(active: true)),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Conflict, """{"code":"last_owner"}"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article button")));

        cut.Find("article button").Click();
        cut.WaitForAssertion(() => Assert.Contains("The last owner cannot be changed.", cut.Markup, StringComparison.Ordinal));

        Assert.DoesNotContain("last_owner", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_owner_is_denied_account_management_without_account_commands()
    {
        var state = await StateAsync(Response(HttpStatusCode.OK, SessionJson(role: "Admin")));
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));

        Assert.Contains("You cannot manage accounts.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("form, article button"));
    }

    [Fact]
    public async Task An_owner_cannot_change_their_own_membership()
    {
        var state = await StateAsync(
            Response(HttpStatusCode.OK, SessionJson(role: "Owner")),
            Response(HttpStatusCode.OK, """
                [
                  {"id":"21ac7193-52fd-4d4b-9194-5cd547448072","username":"Ada","email":"ada@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Admin"},
                  {"id":"77bc7193-52fd-4d4b-9194-5cd547448072","username":"Grace","email":"grace@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Admin"}
                ]
                """));
        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article button")));

        Assert.Contains("Grace", cut.Find("article button").ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Accounts_show_a_loading_state_before_the_session_is_resolved()
    {
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(new Handler()) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader())));

        var cut = Render<AdminAccounts>(parameters => parameters.AddCascadingValue(state));

        Assert.Contains("Loading", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_navigation_separates_page_links_from_account_commands()
    {
        var state = await StateAsync(Response(HttpStatusCode.OK, SessionJson(role: "Owner")));
        var cut = Render<AdminNav>(parameters => parameters.AddCascadingValue(state));

        Assert.NotNull(cut.Find(".admin-nav__pages"));
        Assert.NotNull(cut.Find(".admin-account__menu"));
        Assert.NotNull(cut.Find(".admin-nav__pages a[href='/admin/accounts']"));
        Assert.NotNull(cut.Find(".admin-account__menu a[href='/admin/password']"));
    }

    [Fact]
    public async Task Ordinary_admin_navigation_omits_the_accounts_page()
    {
        var state = await StateAsync(Response(HttpStatusCode.OK, SessionJson(role: "Admin")));
        var cut = Render<AdminNav>(parameters => parameters.AddCascadingValue(state));

        Assert.Empty(cut.FindAll(".admin-nav__pages a[href='/admin/accounts']"));
    }

    [Fact]
    public async Task Temporary_password_and_signed_out_admins_do_not_receive_navigation()
    {
        var temporary = await StateAsync(Response(HttpStatusCode.OK, SessionJson(mustChangePassword: true)));
        var temporaryCut = Render<AdminNav>(parameters => parameters.AddCascadingValue(temporary));
        var signedOut = await StateAsync(Response(HttpStatusCode.Unauthorized));
        var signedOutCut = Render<AdminNav>(parameters => parameters.AddCascadingValue(signedOut));

        Assert.Empty(temporaryCut.FindAll("nav"));
        Assert.Empty(signedOutCut.FindAll("nav"));
    }

    [Fact]
    public async Task Navigation_sign_out_uses_the_admin_api_and_returns_to_login()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson()),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, """{"token":"after"}"""));
        var state = await StateAsync(handler);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<AdminNav>(parameters => parameters.AddCascadingValue(state));

        cut.Find(".admin-account__menu button").Click();
        cut.WaitForAssertion(() => Assert.EndsWith("/admin/login", navigation.Uri, StringComparison.Ordinal));

        Assert.Contains("/api/admin/auth/sign-out", handler.Paths);
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
        => await StateAsync(new Handler(responses));

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(handler)
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

    private static string AccountsJson(bool active) => $$"""
        [{"id":"77bc7193-52fd-4d4b-9194-5cd547448072","username":"Grace","email":"grace@example.test","isActive":true,"isMembershipActive":{{active.ToString().ToLowerInvariant()}},"isPlatform":false,"mustChangePassword":false,"role":"Admin"}]
        """;

    private static string AccountJson(string username, bool array = false)
    {
        var account = $$"""{"id":"77bc7193-52fd-4d4b-9194-5cd547448072","username":"{{username}}","email":"newadmin@example.test","isActive":true,"isMembershipActive":true,"isPlatform":false,"mustChangePassword":false,"role":"Admin"}""";
        return array ? $"[{account}]" : account;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
    {
        Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> Paths => Requests.Select(request => request.Path).ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            return Task.FromResult(responses.Dequeue());
        }
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
            ["admin.accounts.lastOwner"] = "The last owner cannot be changed.",
            ["admin.auth.badUsername"] = "Enter a valid username.",
            ["admin.error"] = "The request could not be completed.",
            ["admin.nav.label"] = "Administration navigation",
            ["admin.nav.home"] = "Home",
            ["admin.nav.accounts"] = "Accounts",
            ["admin.nav.password"] = "Password",
            ["admin.nav.signOut"] = "Sign out",
            ["admin.shell.title"] = "Administration",
        };

        public string this[string key] => Values.TryGetValue(key, out var value) ? value : key;
    }
}
