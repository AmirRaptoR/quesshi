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
    public void Profile_confirmation_prefills_every_tenant_verification_seed()
    {
        UseServices(new PageHandler());

        var cut = Render<ProfileConfirmation>(parameters => parameters.Add(component => component.Confirmation,
            new VerificationResult("profile_needed", "proof", "North player", "avatar-7", "fa")));

        Assert.Equal("North player", cut.Find("#confirmation-name").GetAttribute("value"));
        Assert.Equal("avatar-7", cut.Find("#confirmation-avatar").GetAttribute("value"));
    }

    [Fact]
    public void Enabled_google_sign_in_method_renders_the_google_identity_button()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]"""));

        var cut = Render<SignIn>();

        Assert.Single(cut.FindAll("#google-sign-in"));
    }

    [Fact]
    public void Missing_google_sign_in_method_does_not_render_the_google_identity_button()
    {
        UseServices(new PageHandler("[]"));

        var cut = Render<SignIn>();

        Assert.Empty(cut.FindAll("#google-sign-in"));
    }

    [Fact]
    public async Task Google_identity_button_disappears_when_a_config_refresh_removes_the_method()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(new PageHandler([
            """[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]""",
            "[]"]));
        var cut = Render<SignIn>();

        await Services.GetRequiredService<AppConfig>().ConfigChanged(2);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#google-sign-in")));
    }

    [Fact]
    public void Google_sign_in_method_without_a_usable_client_id_does_not_render_the_google_identity_button()
    {
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"   "}}]"""));

        var cut = Render<SignIn>();

        Assert.Empty(cut.FindAll("#google-sign-in"));
    }

    [Fact]
    public async Task Google_signed_in_response_updates_client_authentication_state()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        var handler = GoogleSignedInHandler();
        var auth = UseServices(handler);
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("google-id-token");

        Assert.Equal(PlayerIdentityStatus.SignedIn, auth.Status);
    }

    [Fact]
    public async Task Google_signed_in_response_navigates_home()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(GoogleSignedInHandler());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/sign-in");
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("google-id-token");

        Assert.EndsWith("/", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_profile_needed_response_opens_confirmation()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        var handler = GoogleProfileNeededHandler();
        UseServices(handler);
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("google-id-token");

        Assert.Equal("North player", cut.Find("#confirmation-name").GetAttribute("value"));
    }

    [Fact]
    public async Task Google_profile_needed_response_does_not_create_a_player()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        var handler = GoogleProfileNeededHandler();
        UseServices(handler);
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("google-id-token");

        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/auth/profile");
    }

    [Fact]
    public async Task Google_button_renders_again_after_cancelling_a_profile_confirmation()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true).SetVoidResult();
        UseServices(GoogleProfileNeededHandler());
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("google-id-token");
        cut.Find("button[type='button']").Click();

        cut.WaitForAssertion(() => Assert.Equal(2,
            JSInterop.Invocations.Count(invocation => invocation.Identifier == "quesshi.google.render")));
    }

    [Fact]
    public async Task Invalid_google_credential_uses_the_existing_sign_in_error()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(InvalidGoogleCredentialHandler());
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("invalid-token");

        Assert.Contains("That did not work.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_google_credential_keeps_emailed_code_sign_in_available()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(InvalidGoogleCredentialHandler());
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("invalid-token");

        Assert.NotNull(cut.Find("#email"));
    }

    [Fact]
    public async Task Invalid_google_credential_keeps_guest_sign_in_available()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(InvalidGoogleCredentialHandler());
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync("invalid-token");

        Assert.NotNull(cut.Find("#guest-name"));
    }

    [Fact]
    public async Task Declined_google_credential_keeps_emailed_code_sign_in_available()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]"""));
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync(null);

        Assert.NotNull(cut.Find("#email"));
    }

    [Fact]
    public async Task Declined_google_credential_keeps_guest_sign_in_available()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true);
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]"""));
        var cut = Render<SignIn>();

        await cut.Instance.GoogleCredentialAsync(null);

        Assert.NotNull(cut.Find("#guest-name"));
    }

    [Fact]
    public void Unavailable_google_identity_services_uses_the_existing_sign_in_error()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true).SetException(new JSException("unavailable"));
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]"""));

        var cut = Render<SignIn>();

        Assert.Contains("That did not work.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_google_identity_services_keeps_emailed_code_sign_in_available()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true).SetException(new JSException("unavailable"));
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]"""));

        var cut = Render<SignIn>();

        Assert.NotNull(cut.Find("#email"));
    }

    [Fact]
    public void Unavailable_google_identity_services_keeps_guest_sign_in_available()
    {
        JSInterop.SetupVoid("quesshi.google.render", _ => true).SetException(new JSException("unavailable"));
        UseServices(new PageHandler("""[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]"""));

        var cut = Render<SignIn>();

        Assert.NotNull(cut.Find("#guest-name"));
    }

    [Fact]
    public void Cancelling_profile_confirmation_returns_to_sign_in_without_creating_a_player()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""),
            Response(HttpStatusCode.OK, """{"status":"profile_needed","token":"proof","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""));
        UseServices(handler);
        var cut = Render<SignIn>();

        cut.Find("#email").Change("ada@example.test");
        cut.Find("form").Submit();
        cut.Find("#code").Change("123456");
        cut.Find("form").Submit();
        cut.Find("button[type='button']").Click();

        Assert.NotNull(cut.Find("#email"));
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
    public void Existing_tenant_player_reaches_home_after_verification()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""),
            Response(HttpStatusCode.OK, """{"status":"signed_in"}"""),
            Response(HttpStatusCode.OK, """{"token":"after"}"""),
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""));
        var auth = UseServices(handler);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/sign-in");
        var cut = Render<SignIn>();

        cut.Find("#email").Change("ada@example.test");
        cut.Find("form").Submit();
        cut.Find("#code").Change("123456");
        cut.Find("form").Submit();

        Assert.Equal(PlayerIdentityStatus.SignedIn, auth.Status);
        Assert.EndsWith("/", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirming_a_profile_posts_the_tenant_seed_and_reaches_home()
    {
        var handler = new PageHandler(
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""),
            Response(HttpStatusCode.OK, """{"status":"profile_needed","token":"proof","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""),
            Response(HttpStatusCode.OK, """{"status":"signed_in"}"""),
            Response(HttpStatusCode.OK, """{"token":"after"}"""),
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""));
        var auth = UseServices(handler);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/sign-in");
        var cut = Render<SignIn>();

        cut.Find("#email").Change("ada@example.test");
        cut.Find("form").Submit();
        cut.Find("#code").Change("123456");
        cut.Find("form").Submit();
        cut.Find("form").Submit();

        Assert.Equal("{\"token\":\"proof\",\"displayName\":\"North player\",\"avatarSeed\":\"avatar-7\",\"lang\":\"fa\"}", handler.Requests.Single(request => request.Path == "/api/auth/profile").Body);
        Assert.Equal(PlayerIdentityStatus.SignedIn, auth.Status);
        Assert.EndsWith("/", navigation.Uri, StringComparison.Ordinal);
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

        Assert.Contains("That email already belongs to another player in this tenant.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(PlayerIdentityStatus.SignedIn, auth.Status);
        Assert.True(auth.Player!.IsGuest);
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
    public async Task Settings_shows_a_language_added_to_the_tenant_configuration()
    {
        var handler = new PageHandler(Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-1","lang":"en"}"""))
            .WithLanguages("""[{"code":"en","displayName":"English","direction":"ltr"},{"code":"de","displayName":"Deutsch","direction":"ltr"}]""");
        var auth = UseServices(handler);
        await auth.InitializeAsync();

        var cut = Render<Settings>();

        Assert.Equal(["en", "de"], cut.FindAll("#settings-language option").Select(option => option.GetAttribute("value")!).ToArray());
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

    private static PageHandler GoogleSignedInHandler() => new(
        """[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]""",
        Response(HttpStatusCode.OK, """{"token":"before"}"""),
        Response(HttpStatusCode.OK, """{"status":"signed_in"}"""),
        Response(HttpStatusCode.OK, """{"token":"after"}"""),
        Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""));

    private static PageHandler GoogleProfileNeededHandler() => new(
        """[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]""",
        Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
        Response(HttpStatusCode.OK, """{"status":"profile_needed","token":"proof","displayName":"North player","avatarSeed":"avatar-7","lang":"fa"}"""));

    private static PageHandler InvalidGoogleCredentialHandler() => new(
        """[{"id":"google","settings":{"clientId":"123.apps.googleusercontent.com"}}]""",
        Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
        Response(HttpStatusCode.Unauthorized, """{"error":"invalid_credential"}"""));

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
        private readonly Queue<string> signInMethodConfigurations;
        private readonly bool guests;
        private string languages = """[{"code":"en","displayName":"English","direction":"ltr"},{"code":"fa","displayName":"فارسی","direction":"rtl"}]""";
        private long configVersion;
        public List<CapturedRequest> Requests { get; } = [];

        public PageHandler(params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
            guests = true;
            signInMethodConfigurations = new Queue<string>(["[]"]);
        }

        public PageHandler(string signInMethods, params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
            guests = true;
            signInMethodConfigurations = new Queue<string>([signInMethods]);
        }

        public PageHandler(IEnumerable<string> signInMethods)
        {
            responses = new Queue<HttpResponseMessage>();
            guests = true;
            signInMethodConfigurations = new Queue<string>(signInMethods);
        }

        public PageHandler(bool guests)
        {
            responses = new Queue<HttpResponseMessage>();
            this.guests = guests;
            signInMethodConfigurations = new Queue<string>(["[]"]);
        }

        public PageHandler WithLanguages(string values)
        {
            languages = values;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri?.PathAndQuery ?? string.Empty, body));
            if (request.RequestUri?.AbsolutePath == "/api/config")
            {
                var switches = guests ? "[\"guests\"]" : "[]";
                var signInMethods = signInMethodConfigurations.Count > 1
                    ? signInMethodConfigurations.Dequeue()
                    : signInMethodConfigurations.Peek();
                return Response(HttpStatusCode.OK, $$"""{"name":"Tenant","languages":{{languages}},"defaultLanguage":"en","capabilities":{"switch":{{switches}}},"signInMethods":{{signInMethods}},"configVersion":{{++configVersion}},"serverBuildId":"test"}""");
            }

            if (request.RequestUri?.AbsolutePath.StartsWith("/i18n/", StringComparison.Ordinal) == true)
            {
                return Response(HttpStatusCode.OK, """{"auth.title":"Sign in","auth.blurb":"No passwords.","auth.email":"Email","auth.send":"Send code","auth.code":"Six-digit code","auth.verify":"Continue","auth.back":"Use another email","auth.badCode":"That code is wrong or expired.","auth.badEmail":"That email doesn't look right.","auth.confirmTitle":"Confirm your profile","auth.confirmBody":"Check details.","auth.confirm":"Confirm profile","auth.cancel":"Cancel","profile.name":"Display name","profile.avatar":"Avatar","profile.language":"Language","profile.save":"Save","profile.saved":"Saved","profile.invalid":"Choose a valid name, avatar, and language.","profile.signOut":"Sign out","auth.guestName":"Guest name","auth.guest":"Continue as guest","auth.upgradeTitle":"Keep this guest profile","auth.upgradeBody":"Add an email.","auth.upgrade":"Upgrade guest account","auth.addressTaken":"That email already belongs to another player in this tenant.","settings.title":"Settings","account.delete":"Delete account","account.deleteBody":"This permanently removes your profile.","account.deletePrompt":"Type DELETE to continue","account.deleteTitle":"Delete account","common.error":"That did not work."}""");
            }

            return responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string Path, string? Body);

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
