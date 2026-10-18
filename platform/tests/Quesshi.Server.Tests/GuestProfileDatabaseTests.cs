using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class GuestProfileDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Guest_entry_honors_capability_antiforgery_and_issues_a_tenant_session()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync(guestsEnabled: false);
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50147") };
        try
        {
            var anonymousToken = await AntiforgeryAsync(client, seeded.Host, null);
            using (var disabled = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/auth/guest", null, anonymousToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "New guest" })))
            {
                Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
            }
            Assert.Equal(1, await GuestCountAsync(seeded.TenantId));
            Assert.Equal(0, await GuestSessionCountAsync(seeded.TenantId));

            await SetGuestsEnabledAsync(seeded.TenantId, enabled: true);
            using (var missingToken = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/auth/guest", null, null, seeded.Origin,
                       JsonContent.Create(new { displayName = "New guest" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
                Assert.Equal("invalid_antiforgery", (await missingToken.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
            }
            using (var foreignOrigin = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/auth/guest", null, anonymousToken, "https://foreign.example.test",
                       JsonContent.Create(new { displayName = "New guest" })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
            }
            using (var invalidName = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/auth/guest", null, anonymousToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "x" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidName.StatusCode);
                Assert.Equal("invalid_profile", (await invalidName.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
            }

            using var created = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/auth/guest", null, anonymousToken, seeded.Origin,
                JsonContent.Create(new { displayName = "  New guest  " }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var profile = (await created.Content.ReadFromJsonAsync<ProfileResponse>())!;
            Assert.Equal("New guest", profile.DisplayName);
            Assert.Equal("avatar-0", profile.AvatarSeed);
            Assert.Equal("en", profile.Lang);
            var cookie = CookieValue(created);
            Assert.Equal(2, await GuestCountAsync(seeded.TenantId));
            Assert.True(await GuestSessionExistsAsync(seeded.TenantId, profile.Id));

            using var staleIdentity = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/auth/guest", cookie, anonymousToken, seeded.Origin,
                JsonContent.Create(new { displayName = "Another guest" }));
            Assert.Equal(HttpStatusCode.BadRequest, staleIdentity.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Account_and_guest_profiles_are_tenant_scoped_and_only_update_their_own_rows()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync(guestsEnabled: true);
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50147") };
        try
        {
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var accountContext = new DefaultHttpContext();
            await sessions.IssueAccountAsync(accountContext, seeded.TenantId, seeded.AccountId);
            var accountCookie = CookieValue(accountContext);
            var accountToken = await AntiforgeryAsync(client, seeded.Host, accountCookie);

            using (var anonymous = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", null, null, null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }
            using (var otherTenant = await SendAsync(client, seeded.OtherHost, HttpMethod.Get, "/api/me", accountCookie, null, null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, otherTenant.StatusCode);
            }
            using (var ownProfile = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", accountCookie, null, null))
            {
                Assert.Equal(HttpStatusCode.OK, ownProfile.StatusCode);
                Assert.Equal(seeded.AccountPlayerId, (await ownProfile.Content.ReadFromJsonAsync<ProfileResponse>())!.Id);
            }
            await ExpireSessionAsync(accountCookie);
            using (var expired = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", accountCookie, null, null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            }
            var refreshedAccountContext = new DefaultHttpContext();
            await sessions.IssueAccountAsync(refreshedAccountContext, seeded.TenantId, seeded.AccountId);
            accountCookie = CookieValue(refreshedAccountContext);
            accountToken = await AntiforgeryAsync(client, seeded.Host, accountCookie);
            using (var missingToken = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, null, seeded.Origin,
                       JsonContent.Create(new { displayName = "Changed account", avatarSeed = "avatar-11", lang = "nl" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
            }
            using (var staleIdentity = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie,
                       await AntiforgeryAsync(client, seeded.Host, null), seeded.Origin,
                       JsonContent.Create(new { displayName = "Changed account", avatarSeed = "avatar-11", lang = "nl" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, staleIdentity.StatusCode);
                Assert.Equal("invalid_antiforgery", (await staleIdentity.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
            }
            using (var foreignOrigin = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, accountToken, "https://foreign.example.test",
                       JsonContent.Create(new { displayName = "Changed account", avatarSeed = "avatar-11", lang = "nl" })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
            }
            using (var invalidName = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, accountToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "x", avatarSeed = "avatar-11", lang = "nl" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidName.StatusCode);
                Assert.Equal("invalid_profile", (await invalidName.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
            }
            using (var invalidAvatar = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, accountToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "Changed account", avatarSeed = "unknown", lang = "nl" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidAvatar.StatusCode);
            }
            using (var unavailableLanguage = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, accountToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "Changed account", avatarSeed = "avatar-11", lang = "de" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, unavailableLanguage.StatusCode);
            }
            using (var avatarOmitted = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, accountToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "Avatar preserved", lang = "en" })))
            {
                Assert.Equal(HttpStatusCode.OK, avatarOmitted.StatusCode);
                var profile = (await avatarOmitted.Content.ReadFromJsonAsync<ProfileResponse>())!;
                Assert.Equal("Avatar preserved", profile.DisplayName);
                Assert.Equal("avatar-0", profile.AvatarSeed);
                Assert.Equal("en", profile.Lang);
            }
            Assert.Equal(("Avatar preserved", "avatar-0", "en"), await PlayerAsync(seeded.TenantId, seeded.AccountPlayerId));
            using (var updated = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", accountCookie, accountToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "  Changed account  ", avatarSeed = "avatar-11", lang = "nl" })))
            {
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                var profile = (await updated.Content.ReadFromJsonAsync<ProfileResponse>())!;
                Assert.Equal("Changed account", profile.DisplayName);
                Assert.Equal("avatar-11", profile.AvatarSeed);
                Assert.Equal("nl", profile.Lang);
            }
            Assert.Equal(("Changed account", "avatar-11", "nl"), await PlayerAsync(seeded.TenantId, seeded.AccountPlayerId));
            Assert.Equal(("Guest player", "avatar-1", "en"), await PlayerAsync(seeded.TenantId, seeded.GuestId));
            Assert.Equal(("Other player", "avatar-2", "en"), await PlayerAsync(seeded.OtherTenantId, seeded.OtherPlayerId));

            var guestContext = new DefaultHttpContext();
            await sessions.IssueGuestAsync(guestContext, seeded.TenantId, seeded.GuestId);
            var guestCookie = CookieValue(guestContext);
            var guestToken = await AntiforgeryAsync(client, seeded.Host, guestCookie);
            using (var guestProfile = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", guestCookie, null, null))
            {
                Assert.Equal(HttpStatusCode.OK, guestProfile.StatusCode);
                Assert.Equal(seeded.GuestId, (await guestProfile.Content.ReadFromJsonAsync<ProfileResponse>())!.Id);
            }
            using (var guestUpdate = await SendAsync(client, seeded.Host, HttpMethod.Put, "/api/me", guestCookie, guestToken, seeded.Origin,
                       JsonContent.Create(new { displayName = "Changed guest", avatarSeed = "avatar-3", lang = "en" })))
            {
                Assert.Equal(HttpStatusCode.OK, guestUpdate.StatusCode);
            }
            Assert.Equal(("Changed guest", "avatar-3", "en"), await PlayerAsync(seeded.TenantId, seeded.GuestId));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private async Task<WebApplication> StartAsync()
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var app = Program.CreateApplication(sources, 11147, 30147);
        app.Urls.Add("http://127.0.0.1:50147");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        return app;
    }

    private async Task<SeededTenant> SeedAsync(bool guestsEnabled)
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var accountPlayerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var otherPlayerId = Guid.NewGuid();
        var host = $"guest-profile-{tenantId:N}.example.test";
        var otherHost = $"guest-profile-{otherTenantId:N}.example.test";
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            insert into tenants(id, slug, name, default_language) values
              (@tenantId, @slug, 'Guest profile', 'en'),
              (@otherTenantId, @otherSlug, 'Other tenant', 'en');
            insert into tenant_hosts(tenant_id, host) values (@tenantId, @host), (@otherTenantId, @otherHost);
            insert into tenant_languages(tenant_id, code, name, english_name) values
              (@tenantId, 'en', 'English', 'English'), (@tenantId, 'nl', 'Nederlands', 'Dutch'),
              (@otherTenantId, 'en', 'English', 'English');
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang) values
              (@tenantId, @accountPlayerId, @accountId, 'Account player', 'avatar-0', 'en'),
              (@tenantId, @guestId, null, 'Guest player', 'avatar-1', 'en'),
              (@otherTenantId, @otherPlayerId, null, 'Other player', 'avatar-2', 'en');
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            values (@tenantId, 'switch', 'guests', true, @guestsEnabled);
            """, new
        {
            tenantId, otherTenantId, accountId, accountPlayerId, guestId, otherPlayerId, host, otherHost,
            slug = $"guest-profile-{tenantId:N}", otherSlug = $"guest-profile-{otherTenantId:N}",
            email = $"guest-profile-{accountId:N}@example.test", guestsEnabled
        });
        return new SeededTenant(tenantId, otherTenantId, accountId, accountPlayerId, guestId, otherPlayerId, host, otherHost);
    }

    private async Task SetGuestsEnabledAsync(Guid tenantId, bool enabled)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("update tenant_capabilities set enabled = @enabled where tenant_id = @tenantId and type = 'switch' and key = 'guests'", new { tenantId, enabled });
    }

    private async Task<int> GuestCountAsync(Guid tenantId)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await owner.QuerySingleAsync<int>("select count(*) from players where tenant_id = @tenantId and account_id is null", new { tenantId });
    }

    private async Task<bool> GuestSessionExistsAsync(Guid tenantId, Guid guestId)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await owner.QuerySingleAsync<bool>("select exists(select 1 from auth_sessions where tenant_id = @tenantId and guest_id = @guestId)", new { tenantId, guestId });
    }

    private async Task<int> GuestSessionCountAsync(Guid tenantId)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await owner.QuerySingleAsync<int>("select count(*) from auth_sessions where tenant_id = @tenantId and guest_id is not null", new { tenantId });
    }

    private async Task ExpireSessionAsync(string cookie)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(cookie));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("update auth_sessions set expires_at = now() - interval '1 minute' where token_hash = @hash", new { hash });
    }

    private async Task<(string DisplayName, string AvatarSeed, string Lang)> PlayerAsync(Guid tenantId, Guid playerId)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await owner.QuerySingleAsync<(string DisplayName, string AvatarSeed, string Lang)>("""
            select display_name as DisplayName, avatar_seed as AvatarSeed, lang as Lang
            from players where tenant_id = @tenantId and id = @playerId
            """, new { tenantId, playerId });
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, string host, string? cookie)
    {
        using var response = await SendAsync(client, host, HttpMethod.Get, "/api/antiforgery", cookie, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string host,
        HttpMethod method,
        string path,
        string? cookie,
        string? csrf,
        string? origin,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qp={cookie}");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF", csrf);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static string CookieValue(DefaultHttpContext context) => CookieValue(context.Response.Headers.SetCookie);

    private static string CookieValue(HttpResponseMessage response) => CookieValue(response.Headers.GetValues("Set-Cookie"));

    private static string CookieValue(IEnumerable<string> headers)
    {
        var header = Assert.Single(headers);
        var value = header["__Host-qp=".Length..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private sealed record SeededTenant(
        Guid TenantId,
        Guid OtherTenantId,
        Guid AccountId,
        Guid AccountPlayerId,
        Guid GuestId,
        Guid OtherPlayerId,
        string Host,
        string OtherHost)
    {
        public string Origin => $"http://{Host}";
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record ErrorResponse(string Error);
    private sealed record ProfileResponse(Guid Id, string DisplayName, string AvatarSeed, string Lang);
}
