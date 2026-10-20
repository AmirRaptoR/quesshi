using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

/// <summary>Starts a migrated platform host with isolated test data and clients for each HTTP identity.</summary>
public sealed class PlatformHttpFixture(params IFeature[] features) : IAsyncDisposable
{
    private const string TenantHost = "platform-http.example.test";
    private const int FirstFixedHttpTestPort = 50000;
    private const int LastFixedHttpTestPort = 50999;
    private static readonly ConcurrentDictionary<int, byte> ReservedPorts = new();
    private readonly PostgresFixture _postgres = new();
    private readonly IReadOnlyList<IFeature> _features = features;
    private readonly List<HttpClient> _clients = [];
    private WebApplication? _application;
    private SeededIdentities? _identities;

    public HttpClient Anonymous { get; private set; } = null!;
    public HttpClient Guest { get; private set; } = null!;
    public HttpClient Player { get; private set; } = null!;
    public HttpClient Moderator { get; private set; } = null!;
    public HttpClient Editor { get; private set; } = null!;
    public HttpClient Owner { get; private set; } = null!;
    public HttpClient PlatformAdministrator { get; private set; } = null!;
    public string DatabaseName => _postgres.DatabaseName ?? throw new InvalidOperationException("The platform HTTP fixture has not initialized its database.");
    public Guid TenantId => _identities?.TenantId ?? throw new InvalidOperationException("The platform HTTP fixture has not initialized its identities.");
    public Guid PlayerId => _identities?.PlayerId ?? throw new InvalidOperationException("The platform HTTP fixture has not initialized its identities.");
    public int HttpPort { get; private set; }
    public int SiloPort { get; private set; }
    public int GatewayPort { get; private set; }
    public Uri TenantOrigin => new($"http://{TenantHost}");

    public async Task SetCapabilityAsync(string type, string key, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var owner = new NpgsqlConnection(_postgres.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync(cancellationToken);
        await owner.ExecuteAsync(new CommandDefinition("""
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            values (@tenantId, @type, @key, true, @enabled)
            on conflict (tenant_id, type, key) do update set entitled = true, enabled = excluded.enabled
            """, new { tenantId = TenantId, type, key, enabled }, cancellationToken: cancellationToken));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null) throw new InvalidOperationException("The platform HTTP fixture has already started.");
        if (_features.Count == 0) throw new InvalidOperationException("At least one test feature is required.");

        await _postgres.InitializeAsync();
        await Program.MigrateAsync(_postgres.ConnectionStringForRole("quesshi_owner"), cancellationToken);
        _identities = await SeedAsync(cancellationToken);

        HttpPort = AllocatePort();
        SiloPort = AllocatePort();
        GatewayPort = AllocatePort();
        var sources = new DatabaseSources(
            _postgres.ConnectionStringForRole("quesshi_app"),
            _postgres.ConnectionStringForRole("quesshi_system"));
        WebApplication? application = null;
        try
        {
            application = Program.CreateApplication(
                sources,
                SiloPort,
                GatewayPort,
                configuredFeatures: FeatureRegistry.Create(_features));
            application.Urls.Add($"http://127.0.0.1:{HttpPort}");
            await application.StartAsync(cancellationToken);
            await application.Services.GetRequiredService<PostgresClock>().StartAsync(cancellationToken);

            _application = application;
            var address = new Uri(application.Urls.Single());
            if (address.Port != HttpPort) throw new InvalidOperationException("The platform host did not bind its allocated HTTP port.");
            Anonymous = await CreatePlayerClientAsync(null, cancellationToken);
            Guest = await CreateGuestClientAsync(cancellationToken);
            Player = await CreatePlayerClientAsync(_identities.AccountId, cancellationToken);
            Moderator = await CreateAdminClientAsync(_identities.ModeratorAdminId, cancellationToken);
            Editor = await CreateAdminClientAsync(_identities.EditorAdminId, cancellationToken);
            Owner = await CreateAdminClientAsync(_identities.OwnerAdminId, cancellationToken);
            PlatformAdministrator = await CreateAdminClientAsync(_identities.PlatformAdministratorId, cancellationToken);
        }
        catch
        {
            try
            {
                if (application is null) await sources.DisposeAsync();
                else await application.DisposeAsync();
            }
            finally
            {
                ReleasePorts();
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) client.Dispose();
        try
        {
            if (_application is not null)
            {
                await _application.StopAsync();
                await _application.DisposeAsync();
            }
            await _postgres.DisposeAsync();
        }
        finally
        {
            ReleasePorts();
        }
    }

    /// <summary>Creates an authenticated player in an isolated tenant for tenant-bound route tests.</summary>
    public async Task<HttpClient> CreateOtherTenantPlayerClientAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var host = $"other-{tenantId:N}.example.test";
        await using var owner = new NpgsqlConnection(_postgres.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync(cancellationToken);
        await owner.ExecuteAsync(new CommandDefinition("""
            insert into tenants(id, slug, name, default_language)
            values (@tenantId, @slug, 'Other HTTP fixture', 'en');
            insert into tenant_hosts(tenant_id, host) values (@tenantId, @host);
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, 'Other fixture player', 'other', 'en');
            """, new
        {
            tenantId,
            slug = $"other-{tenantId:N}",
            host,
            accountId,
            email = $"other-{accountId:N}@example.test",
            playerId,
        }, cancellationToken: cancellationToken));
        return await CreatePlayerClientAsync(tenantId, accountId, host, cancellationToken);
    }

    private async Task<HttpClient> CreatePlayerClientAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        var cookie = accountId is null ? null : await IssueAccountCookieAsync(_identities!.TenantId, accountId.Value, cancellationToken);
        var token = await AntiforgeryAsync("/api/antiforgery", cookie, "__Host-qp", TenantHost, cancellationToken);
        return AddClient(cookie, token, "__Host-qp", "X-CSRF", TenantHost);
    }

    private async Task<HttpClient> CreatePlayerClientAsync(Guid tenantId, Guid accountId, string host, CancellationToken cancellationToken)
    {
        var cookie = await IssueAccountCookieAsync(tenantId, accountId, cancellationToken);
        var token = await AntiforgeryAsync("/api/antiforgery", cookie, "__Host-qp", host, cancellationToken);
        return AddClient(cookie, token, "__Host-qp", "X-CSRF", host);
    }

    private async Task<HttpClient> CreateGuestClientAsync(CancellationToken cancellationToken)
    {
        var cookie = await IssueGuestCookieAsync(_identities!.GuestId, cancellationToken);
        var token = await AntiforgeryAsync("/api/antiforgery", cookie, "__Host-qp", TenantHost, cancellationToken);
        return AddClient(cookie, token, "__Host-qp", "X-CSRF", TenantHost);
    }

    private async Task<HttpClient> CreateAdminClientAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var cookie = await IssueAdminCookieAsync(adminId, cancellationToken);
        var token = await AntiforgeryAsync("/api/admin/antiforgery", cookie, "__Host-qa", TenantHost, cancellationToken);
        return AddClient(cookie, token, "__Host-qa", "X-CSRF-Admin", TenantHost);
    }

    private HttpClient AddClient(string? cookie, string token, string cookieName, string antiforgeryHeader, string host)
    {
        var client = new HttpClient(new TenantRequestHandler(host, new Uri($"http://{host}"), cookie, token, cookieName, antiforgeryHeader))
        {
            BaseAddress = new Uri($"http://127.0.0.1:{HttpPort}"),
        };
        _clients.Add(client);
        return client;
    }

    private async Task<string> AntiforgeryAsync(string path, string? cookie, string cookieName, string host, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HttpPort}") };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={cookie}");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>(cancellationToken))?.Token
            ?? throw new InvalidOperationException("The platform host returned no antiforgery token.");
    }

    private async Task<string> IssueAccountCookieAsync(Guid tenantId, Guid accountId, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext();
        await _application!.Services.GetRequiredService<IPlayerSessionIssuer>()
            .IssueAccountAsync(context, tenantId, accountId, cancellationToken);
        return CookieValue(context, "__Host-qp");
    }

    private async Task<string> IssueGuestCookieAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext();
        await _application!.Services.GetRequiredService<IPlayerSessionIssuer>()
            .IssueGuestAsync(context, _identities!.TenantId, playerId, cancellationToken);
        return CookieValue(context, "__Host-qp");
    }

    private async Task<string> IssueAdminCookieAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext();
        var issued = await _application!.Services.GetRequiredService<IAdminSessionIssuer>()
            .IssueAsync(context, _identities!.TenantId, adminId, mustChangePassword: false, cancellationToken);
        if (!issued) throw new InvalidOperationException("The platform host did not issue the requested admin session.");
        return CookieValue(context, "__Host-qa");
    }

    private async Task<SeededIdentities> SeedAsync(CancellationToken cancellationToken)
    {
        var identities = SeededIdentities.Create();
        await using var owner = new NpgsqlConnection(_postgres.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync(cancellationToken);
        await owner.ExecuteAsync(new CommandDefinition("""
            insert into tenants(id, slug, name, default_language)
            values (@tenantId, @slug, 'Platform HTTP fixture', 'en');
            insert into tenant_hosts(tenant_id, host) values (@tenantId, @host);
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, 'Fixture player', 'player', 'en'),
                   (@tenantId, @guestId, null, 'Fixture guest', 'guest', 'en');
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            select @tenantId, 'feature', unnest(@featureIds), true, true;
            insert into admins(id, username, email, password_hash, must_change_password, is_platform)
            values (@ownerAdminId, @ownerUsername, @ownerEmail, 'hash', false, false),
                   (@editorAdminId, @editorUsername, @editorEmail, 'hash', false, false),
                   (@moderatorAdminId, @moderatorUsername, @moderatorEmail, 'hash', false, false),
                   (@platformAdministratorId, @platformUsername, @platformEmail, 'hash', false, true);
            insert into admin_memberships(tenant_id, admin_id, role)
            values (@tenantId, @ownerAdminId, 'owner'),
                   (@tenantId, @editorAdminId, 'editor'),
                   (@tenantId, @moderatorAdminId, 'moderator');
            """, new
        {
            tenantId = identities.TenantId,
            slug = $"fixture-{identities.TenantId:N}",
            host = TenantHost,
            accountId = identities.AccountId,
            email = $"fixture-{identities.AccountId:N}@example.test",
            playerId = identities.PlayerId,
            guestId = identities.GuestId,
            featureIds = _features.Select(feature => feature.Id).ToArray(),
            ownerAdminId = identities.OwnerAdminId,
            editorAdminId = identities.EditorAdminId,
            moderatorAdminId = identities.ModeratorAdminId,
            platformAdministratorId = identities.PlatformAdministratorId,
            ownerUsername = $"owner-{identities.OwnerAdminId:N}",
            ownerEmail = $"owner-{identities.OwnerAdminId:N}@example.test",
            editorUsername = $"editor-{identities.EditorAdminId:N}",
            editorEmail = $"editor-{identities.EditorAdminId:N}@example.test",
            moderatorUsername = $"moderator-{identities.ModeratorAdminId:N}",
            moderatorEmail = $"moderator-{identities.ModeratorAdminId:N}@example.test",
            platformUsername = $"platform-{identities.PlatformAdministratorId:N}",
            platformEmail = $"platform-{identities.PlatformAdministratorId:N}@example.test",
        }, cancellationToken: cancellationToken));
        return identities;
    }

    private static string CookieValue(DefaultHttpContext context, string cookieName)
    {
        var header = context.Response.Headers.SetCookie.SingleOrDefault(value => value is not null && value.StartsWith($"{cookieName}=", StringComparison.Ordinal));
        if (header is null) throw new InvalidOperationException("The platform host did not write a session cookie.");
        var value = header[(cookieName.Length + 1)..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private static int AllocatePort()
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port is >= FirstFixedHttpTestPort and <= LastFixedHttpTestPort) continue;
            if (ReservedPorts.TryAdd(port, 0)) return port;
        }
    }

    private void ReleasePorts()
    {
        if (HttpPort != 0) ReservedPorts.TryRemove(HttpPort, out _);
        if (SiloPort != 0) ReservedPorts.TryRemove(SiloPort, out _);
        if (GatewayPort != 0) ReservedPorts.TryRemove(GatewayPort, out _);
    }

    private sealed class TenantRequestHandler(string host, Uri origin, string? cookie, string token, string cookieName, string antiforgeryHeader) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = host;
            request.Headers.TryAddWithoutValidation("Origin", origin.OriginalString);
            if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={cookie}");
            request.Headers.TryAddWithoutValidation(antiforgeryHeader, token);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record AntiforgeryResponse(string Token);

    private sealed record SeededIdentities(
        Guid TenantId,
        Guid AccountId,
        Guid PlayerId,
        Guid GuestId,
        Guid OwnerAdminId,
        Guid EditorAdminId,
        Guid ModeratorAdminId,
        Guid PlatformAdministratorId)
    {
        public static SeededIdentities Create() => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
    }
}
