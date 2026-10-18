using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quesshi.Web.Models;
using Quesshi.Web.Pages;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PlayerIdentityPageTests : BunitContext
{
    [Fact]
    public void Profile_confirmation_prefills_the_tenant_verification_response()
    {
        UseServices(new PageHandler());

        var cut = Render<ProfileConfirmation>(parameters => parameters.Add(component => component.Confirmation,
            new VerificationResult("profile_needed", "proof", "North player", "avatar-7", "fa")));

        Assert.Equal("North player", cut.Find("#confirmation-name").GetAttribute("value"));
    }

    [Fact]
    public void Cancelling_profile_confirmation_does_not_create_a_player()
    {
        var handler = new PageHandler();
        UseServices(handler);
        var cancelled = false;
        var cut = Render<ProfileConfirmation>(parameters => parameters
            .Add(component => component.Confirmation, new VerificationResult("profile_needed", "proof", "North player", "avatar-7", "fa"))
            .Add(component => component.OnCancelled, EventCallback.Factory.Create(this, () => cancelled = true)));

        cut.Find("button[type='button']").Click();

        Assert.True(cancelled);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/auth/profile");
    }

    [Fact]
    public void Invalid_verification_stays_on_the_sign_in_flow_with_a_localized_error()
    {
        UseServices(new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""),
            Response(HttpStatusCode.Unauthorized, """{"error":"invalid_code"}""")));
        var cut = Render<SignIn>();

        cut.Find("#email").Change("ada@example.test");
        cut.Find("form").Submit();
        cut.Find("#code").Change("123456");
        cut.Find("form").Submit();

        Assert.Contains("That code is wrong or expired.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void First_tenant_visit_opens_confirmation_without_creating_a_player()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""),
            Response(HttpStatusCode.OK, """{"status":"profile_needed","token":"proof","displayName":"Second tenant","avatarSeed":"avatar-9","lang":"fa"}"""));
        UseServices(handler);
        var cut = Render<SignIn>();

        cut.Find("#email").Change("ada@example.test");
        cut.Find("form").Submit();
        cut.Find("#code").Change("123456");
        cut.Find("form").Submit();

        Assert.Equal("Second tenant", cut.Find("#confirmation-name").GetAttribute("value"));
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/auth/profile");
    }

    [Fact]
    public void Confirming_a_profile_creates_it_only_after_the_form_is_submitted()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.OK, """{"status":"signed_in"}"""),
            Response(HttpStatusCode.OK, """{"token":"after"}"""),
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""));
        UseServices(handler);
        var cut = Render<ProfileConfirmation>(parameters => parameters.Add(component => component.Confirmation,
            new VerificationResult("profile_needed", "proof", "North player", "avatar-7", "fa")));

        cut.Find("form").Submit();

        Assert.Contains(handler.Requests, request => request.Path == "/api/auth/profile");
    }

    [Fact]
    public void Guest_entry_is_hidden_when_the_tenant_disables_guests()
    {
        UseServices(new PageHandler(guests: false));

        var cut = Render<SignIn>();

        Assert.Empty(cut.FindAll("#guest-name"));
    }

    [Fact]
    public async Task Address_taken_keeps_the_guest_signed_in()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.Created, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Guest","avatarSeed":"avatar-1","lang":"en"}"""),
            Response(HttpStatusCode.OK, """{"token":"after-guest"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""),
            Response(HttpStatusCode.Conflict, """{"error":"address_taken"}"""));
        var auth = UseServices(handler);
        await auth.CreateGuestAsync("Guest");
        var expectedPlayerId = auth.Player?.Id;
        var cut = Render<UpgradeGuest>();

        cut.Find("#upgrade-email").Change("ada@example.test");
        cut.Find("form").Submit();
        cut.Find("#upgrade-code").Change("123456");
        cut.Find("form").Submit();

        Assert.Equal(expectedPlayerId, auth.Player?.Id);
    }

    [Fact]
    public async Task Settings_saves_the_current_tenant_player_profile()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Grace","avatarSeed":"avatar-7","lang":"fa"}"""));
        var auth = UseServices(handler);
        await auth.InitializeAsync();
        var cut = Render<Settings>();

        cut.Find("#settings-name").Change("Grace");
        cut.Find("#settings-avatar").Change("avatar-7");
        cut.Find("#settings-language").Change("fa");
        cut.Find("form").Submit();

        Assert.Equal("Grace", auth.Player?.DisplayName);
    }

    [Fact]
    public async Task Profile_shows_the_current_tenant_player_identity()
    {
        var auth = UseServices(new PageHandler(Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}""")));
        await auth.InitializeAsync();

        var cut = Render<Profile>();

        Assert.Contains("Ada", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_shows_a_localized_server_validation_error()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest),
            Response(HttpStatusCode.OK, """{"token":"fresh"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_profile"}"""));
        var auth = UseServices(handler);
        await auth.InitializeAsync();
        var cut = Render<Settings>();

        cut.Find("form").Submit();

        Assert.Contains("Choose a valid name, avatar, and language.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_sign_out_clears_the_current_player()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}"""),
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, """{"token":"after"}"""));
        var auth = UseServices(handler);
        await auth.InitializeAsync();
        var cut = Render<Settings>();

        cut.Find("#sign-out").Click();

        Assert.Equal(PlayerIdentityStatus.SignedOut, auth.Status);
    }

    [Fact]
    public async Task Account_deletion_requires_the_typed_confirmation()
    {
        var handler = new PageHandler(Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}"""));
        var auth = UseServices(handler);
        await auth.InitializeAsync();
        var cut = Render<Settings>();

        Assert.True(cut.Find("#delete-account").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Confirmed_account_deletion_clears_the_current_player()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}"""),
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, """{"token":"after"}"""));
        var auth = UseServices(handler);
        await auth.InitializeAsync();
        var cut = Render<Settings>();

        cut.Find("#delete-confirmation").Change("DELETE");
        cut.Find("#delete-account").Click();

        Assert.Equal(PlayerIdentityStatus.SignedOut, auth.Status);
    }

    private AuthState UseServices(PageHandler handler)
    {
        var http = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, new NullJs());
        config.LoadAsync().GetAwaiter().GetResult();
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        Services.AddSingleton(config);
        Services.AddSingleton(translator);
        Services.AddSingleton(new LanguageSelection(config, translator, new NullJs()));
        var auth = new AuthState(new PlayerApi(http));
        Services.AddSingleton(auth);
        return auth;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
    {
        Content = json is null ? new StringContent(string.Empty) : new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class PageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;
        private readonly bool guests;
        public List<CapturedRequest> Requests { get; } = [];

        public PageHandler(params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
            guests = true;
        }

        public PageHandler(bool guests)
        {
            responses = new Queue<HttpResponseMessage>();
            this.guests = guests;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri?.PathAndQuery ?? string.Empty));
            if (request.RequestUri?.AbsolutePath == "/api/config")
            {
                var switches = guests ? "[\"guests\"]" : "[]";
                return Task.FromResult(Response(HttpStatusCode.OK, $$"""{"name":"Tenant","languages":[{"code":"en","displayName":"English","direction":"ltr"},{"code":"fa","displayName":"فارسی","direction":"rtl"}],"defaultLanguage":"en","capabilities":{"switch":{{switches}}},"configVersion":1,"serverBuildId":"test"}"""));
            }

            if (request.RequestUri?.AbsolutePath.StartsWith("/i18n/", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(Response(HttpStatusCode.OK, """{"auth.title":"Sign in","auth.blurb":"No passwords.","auth.email":"Email","auth.send":"Send code","auth.code":"Six-digit code","auth.verify":"Continue","auth.back":"Use another email","auth.badCode":"That code is wrong or expired.","auth.badEmail":"That email doesn't look right.","auth.confirmTitle":"Confirm your profile","auth.confirmBody":"Check details.","auth.confirm":"Confirm profile","auth.cancel":"Cancel","profile.name":"Display name","profile.avatar":"Avatar","profile.language":"Language","profile.save":"Save","profile.saved":"Saved","profile.invalid":"Choose a valid name, avatar, and language.","profile.signOut":"Sign out","auth.guestName":"Guest name","auth.guest":"Continue as guest","auth.upgradeTitle":"Keep this guest profile","auth.upgradeBody":"Add an email.","auth.upgrade":"Upgrade guest account","auth.addressTaken":"That email already belongs to another player in this tenant.","settings.title":"Settings","account.delete":"Delete account","account.deleteBody":"This permanently removes your profile.","account.deletePrompt":"Type DELETE to continue","account.deleteTitle":"Delete account","common.error":"That did not work."}"""));
            }

            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed record CapturedRequest(string Path);

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NullJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
