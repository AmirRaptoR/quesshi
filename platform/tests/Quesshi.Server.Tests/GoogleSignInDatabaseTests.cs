using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application;
using Quesshi.Features.SignIn.Google;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class GoogleSignInDatabaseTests(PostgresFixture fixture)
{
    private const string ClientId = "123456.apps.googleusercontent.com";
    private static readonly DateTimeOffset TokenNow = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
    private static readonly RSA SigningKey = RSA.Create(2048);

    [SkippableFact]
    public async Task Google_route_applies_open_route_policies_and_hides_off_tenants()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var enabled = await SeedAsync(entitled: true, enabled: true);
        var disabled = await SeedAsync(entitled: true, enabled: false);
        var unentitled = await SeedAsync(entitled: false, enabled: true);
        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11154, 30154);
        app.Urls.Add("http://127.0.0.1:50154");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50154") };
            var token = CreateToken("policy-subject", "policy@example.test", true);

            using (var missingAntiforgery = await PostAsync(client, enabled, token, null, TenantOrigin(enabled)))
                Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

            using (var foreignOrigin = await PostAsync(client, enabled, token, await AntiforgeryAsync(client, enabled), "https://foreign.example.test"))
                Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);

            using (var disabledResponse = await PostAsync(client, disabled, token, await AntiforgeryAsync(client, disabled), TenantOrigin(disabled)))
                Assert.Equal(HttpStatusCode.NotFound, disabledResponse.StatusCode);

            using var unentitledResponse = await PostAsync(client, unentitled, token, await AntiforgeryAsync(client, unentitled), TenantOrigin(unentitled));
            Assert.Equal(HttpStatusCode.NotFound, unentitledResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Invalid_google_token_has_no_identity_or_session_side_effects()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var tenant = await SeedAsync(entitled: true, enabled: true);
        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11155, 30155);
        app.Urls.Add("http://127.0.0.1:50155");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            var before = await StateAsync(owner);
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50155") };
            using var invalidSigningKey = RSA.Create(2048);
            using var response = await PostAsync(
                client,
                tenant,
                CreateToken("invalid-subject", "invalid@example.test", true, invalidSigningKey),
                await AntiforgeryAsync(client, tenant),
                TenantOrigin(tenant));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(before, await StateAsync(owner));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableTheory]
    [InlineData("empty")]
    [InlineData("missing_token")]
    [InlineData("blank_token")]
    [InlineData("malformed_json")]
    [InlineData("not_a_jwt")]
    [InlineData("text_plain")]
    public async Task Malformed_google_requests_have_no_identity_or_session_side_effects(string requestKind)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var tenant = await SeedAsync(entitled: true, enabled: true);
        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11160, 30160);
        app.Urls.Add("http://127.0.0.1:50160");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            var before = await StateAsync(owner);
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50160") };
            using var response = await PostContentAsync(
                client,
                tenant,
                CreateMalformedContent(requestKind),
                await AntiforgeryAsync(client, tenant),
                TenantOrigin(tenant));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(before, await StateAsync(owner));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Valid_google_tokens_return_profile_needed_or_signed_in_for_the_tenant_account()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var tenant = await SeedAsync(entitled: true, enabled: true);
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
        {
            await owner.OpenAsync();
            await owner.ExecuteAsync("""
                insert into accounts(id, email) values (@accountId, 'live-google@example.test');
                insert into account_identities(provider, subject, account_id) values ('google', 'live-google-subject', @accountId);
                insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
                values (@tenantId, @playerId, @accountId, 'Live Google', 'live-google', 'en')
                """, new { accountId, playerId, tenantId = tenant.Id });
        }

        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11156, 30156);
        app.Urls.Add("http://127.0.0.1:50156");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50156") };
            using (var profileNeeded = await PostAsync(
                       client,
                       tenant,
                       CreateToken("new-google-subject", "new-google@example.test", true),
                       await AntiforgeryAsync(client, tenant),
                       TenantOrigin(tenant)))
            {
                using var document = JsonDocument.Parse(await profileNeeded.Content.ReadAsStringAsync());
                Assert.Equal("profile_needed", document.RootElement.GetProperty("status").GetString());
            }

            using var signedIn = await PostAsync(
                client,
                tenant,
                CreateToken("live-google-subject", "changed-email@example.test", true),
                await AntiforgeryAsync(client, tenant),
                TenantOrigin(tenant));
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-qp=", StringComparison.Ordinal));
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(accountId, await owner.QuerySingleAsync<Guid>("""
                select account_id from account_identities
                where provider = 'google' and subject = 'live-google-subject'
                """));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Verified_google_email_links_a_live_tenant_account()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var tenant = await SeedAsync(entitled: true, enabled: true);
        var email = $"held-{Guid.NewGuid():N}@example.test";
        var accountId = await SeedLiveAccountAsync(tenant, email);
        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11158, 30158);
        app.Urls.Add("http://127.0.0.1:50158");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50158") };
            using var response = await PostAsync(
                client,
                tenant,
                CreateToken("verified-held-subject", email, true),
                await AntiforgeryAsync(client, tenant),
                TenantOrigin(tenant));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(accountId, await owner.QuerySingleAsync<Guid>("""
                select account_id from account_identities
                where provider = 'google' and subject = 'verified-held-subject'
                """));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Unverified_google_email_never_links_a_held_address()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var tenant = await SeedAsync(entitled: true, enabled: true);
        var email = $"held-{Guid.NewGuid():N}@example.test";
        var accountId = await SeedLiveAccountAsync(tenant, email);
        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11159, 30159);
        app.Urls.Add("http://127.0.0.1:50159");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50159") };
            using var response = await PostAsync(
                client,
                tenant,
                CreateToken("unverified-held-subject", email, false),
                await AntiforgeryAsync(client, tenant),
                TenantOrigin(tenant));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            var subjectAccount = await owner.QuerySingleAsync<Guid>("""
                select account_id from account_identities
                where provider = 'google' and subject = 'unverified-held-subject'
                """);
            Assert.NotEqual(accountId, subjectAccount);
            Assert.True(await owner.QuerySingleAsync<bool>("select email is null from accounts where id = @subjectAccount", new { subjectAccount }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Config_projects_only_enabled_public_google_settings_and_tracks_changes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var enabled = await SeedAsync(entitled: true, enabled: true);
        var unentitled = await SeedAsync(entitled: false, enabled: true);
        await using var sources = CreateSources();
        await using var app = CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            using var first = await GetConfigAsync(client, enabled);
            using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var firstVersion = firstDocument.RootElement.GetProperty("configVersion").GetInt64();
            var method = Assert.Single(firstDocument.RootElement.GetProperty("signInMethods").EnumerateArray());
            Assert.Equal("google", method.GetProperty("id").GetString());
            Assert.Equal(ClientId, method.GetProperty("settings").GetProperty("clientId").GetString());
            Assert.False(method.GetProperty("settings").TryGetProperty("clientSecret", out _));

            await app.Services.GetRequiredService<ICapabilities>().SetAsync(
                enabled.Id,
                CapabilityType.Signin,
                "google",
                true,
                new GoogleSignInSettings("654321.apps.googleusercontent.com"));

            using var updated = await GetConfigAsync(client, enabled);
            using var updatedDocument = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            Assert.Equal("654321.apps.googleusercontent.com", updatedDocument.RootElement.GetProperty("signInMethods")[0].GetProperty("settings").GetProperty("clientId").GetString());
            Assert.True(updatedDocument.RootElement.GetProperty("configVersion").GetInt64() > firstVersion);

            await app.Services.GetRequiredService<ICapabilities>().SetAsync(
                enabled.Id,
                CapabilityType.Signin,
                "google",
                false,
                new GoogleSignInSettings("654321.apps.googleusercontent.com"));

            using var disabled = await GetConfigAsync(client, enabled);
            using var disabledDocument = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
            Assert.Empty(disabledDocument.RootElement.GetProperty("signInMethods").EnumerateArray());

            using var notEntitled = await GetConfigAsync(client, unentitled);
            using var notEntitledDocument = JsonDocument.Parse(await notEntitled.Content.ReadAsStringAsync());
            Assert.Empty(notEntitledDocument.RootElement.GetProperty("signInMethods").EnumerateArray());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private DatabaseSources CreateSources() => new(
        fixture.ConnectionStringForRole("quesshi_app"),
        fixture.ConnectionStringForRole("quesshi_system"));

    private static WebApplication CreateApplication(DatabaseSources sources, int siloPort, int gatewayPort) => Program.CreateApplication(
        sources,
        siloPort,
        gatewayPort,
        configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(TokenNow));
            services.AddHttpClient<GoogleIdTokenValidator>()
                .ConfigurePrimaryHttpMessageHandler(static () => new KeySetHandler(SigningKey.ExportParameters(false)));
        });

    private async Task<GoogleTenant> SeedAsync(bool entitled, bool enabled)
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenant = new GoogleTenant(Guid.NewGuid(), $"google-{Guid.NewGuid():N}.example.test");
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            insert into tenants(id, slug, name, default_language)
            values (@tenantId, @slug, 'Google tenant', 'en');
            insert into tenant_hosts(tenant_id, host) values (@tenantId, @host);
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
            values (@tenantId, 'signin', 'google', @entitled, @enabled, cast(@settings as jsonb))
            """, new
        {
            tenantId = tenant.Id,
            slug = $"google-{tenant.Id:N}",
            host = tenant.Host,
            entitled,
            enabled,
            settings = JsonSerializer.Serialize(new GoogleSignInSettings(ClientId))
        });
        return tenant;
    }

    private async Task<Guid> SeedLiveAccountAsync(GoogleTenant tenant, string email)
    {
        var accountId = Guid.NewGuid();
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, 'Live Google', 'live-google', 'en')
            """, new { tenantId = tenant.Id, accountId, playerId = Guid.NewGuid(), email });
        return accountId;
    }

    private static async Task<IdentityState> StateAsync(NpgsqlConnection owner) => await owner.QuerySingleAsync<IdentityState>("""
        select
            (select count(*) from accounts) as Accounts,
            (select count(*) from account_identities) as Identities,
            (select count(*) from players) as Players,
            (select count(*) from auth_sessions) as Sessions
        """);

    private static async Task<string> AntiforgeryAsync(HttpClient client, GoogleTenant tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
        request.Headers.Host = tenant.Host;
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, GoogleTenant tenant, string idToken, string? antiforgery, string origin)
    {
        return await PostContentAsync(client, tenant, JsonContent.Create(new { idToken }), antiforgery, origin);
    }

    private static async Task<HttpResponseMessage> PostContentAsync(HttpClient client, GoogleTenant tenant, HttpContent content, string? antiforgery, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/x/google") { Content = content };
        request.Headers.Host = tenant.Host;
        request.Headers.TryAddWithoutValidation("Origin", origin);
        if (antiforgery is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF", antiforgery);
        }
        return await client.SendAsync(request);
    }

    private static HttpContent CreateMalformedContent(string requestKind) => requestKind switch
    {
        "empty" => new ByteArrayContent([]),
        "missing_token" => JsonContent.Create(new { }),
        "blank_token" => JsonContent.Create(new { idToken = "  " }),
        "malformed_json" => new StringContent("{", Encoding.UTF8, "application/json"),
        "not_a_jwt" => JsonContent.Create(new { idToken = "not-a-jwt" }),
        "text_plain" => new StringContent("{\"idToken\":\"not-a-jwt\"}", Encoding.UTF8, "text/plain"),
        _ => throw new ArgumentOutOfRangeException(nameof(requestKind))
    };

    private static async Task<HttpResponseMessage> GetConfigAsync(HttpClient client, GoogleTenant tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        request.Headers.Host = tenant.Host;
        return await client.SendAsync(request);
    }

    private static string TenantOrigin(GoogleTenant tenant) => $"http://{tenant.Host}";

    private static string CreateToken(string subject, string email, bool emailVerified, RSA? signingKey = null)
    {
        var key = signingKey ?? SigningKey;
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "test-key" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = subject,
            iss = "https://accounts.google.com",
            aud = ClientId,
            exp = TokenNow.AddMinutes(5).ToUnixTimeSeconds(),
            email,
            email_verified = emailVerified
        }));
        var signed = string.Concat(header, ".", payload);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return string.Concat(signed, ".", Encode(signature));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class KeySetHandler(RSAParameters parameters) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kid = "test-key", kty = "RSA", n = Encode(parameters.Modulus!), e = Encode(parameters.Exponent!) }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed record GoogleTenant(Guid Id, string Host);
    private sealed record IdentityState(long Accounts, long Identities, long Players, long Sessions);
    private sealed record AntiforgeryResponse(string Token);
}
