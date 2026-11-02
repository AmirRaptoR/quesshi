using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests.StaticPages;

/// <summary>
/// The invite page's hand-over to the WebAssembly app, driven in a real browser against the real server and
/// database. The browser script serves the published client itself and forwards everything else to the server.
/// </summary>
public sealed class JoinTakeoverTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new TakeoverFeature()]);
        await _host.StartAsync();
        await _host.ExecuteOwnerAsync("""
            update tenants set name = 'Test Quiz', default_language = 'en' where id = @t;
            insert into tenant_languages(tenant_id, code, name, english_name, direction, sort_order)
            values (@t, 'en', 'English', 'English', 'ltr', 0);
            """, new { t = _host.TenantId });
        await _host.SetCapabilityAsync("format", "async_duel", true);
        await _host.SetCapabilityAsync("switch", "guests", true);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Pressing_join_before_the_app_is_ready_sends_nothing_and_then_makes_exactly_one_join_call_and_one_seat()
    {
        var lobby = await CreateLobbyAsync();
        var (cookie, playerId) = await Host.CreateSecondPlayerBrowserSessionAsync();

        var joinRequests = await RunAsync("held", lobby.Code, cookie);

        Assert.Equal(1, joinRequests);
        Assert.Equal(1, await SeatsAsync(lobby.Id, playerId));
    }

    [SkippableFact]
    public async Task When_the_app_is_ready_it_removes_the_preview_and_shows_itself_without_joining_unprompted()
    {
        var lobby = await CreateLobbyAsync();
        var (cookie, playerId) = await Host.CreateSecondPlayerBrowserSessionAsync();

        var joinRequests = await RunAsync("ready", lobby.Code, cookie);

        Assert.Equal(0, joinRequests);
        Assert.Equal(0, await SeatsAsync(lobby.Id, playerId));
    }

    [SkippableFact]
    public async Task Takeover_joins_the_lobby_for_a_signed_in_player()
    {
        var lobby = await CreateLobbyAsync();
        var (cookie, playerId) = await Host.CreateSecondPlayerBrowserSessionAsync();

        var joinRequests = await RunAsync("signedIn", lobby.Code, cookie);

        Assert.Equal(1, joinRequests);
        Assert.Equal(1, await SeatsAsync(lobby.Id, playerId));
    }

    [SkippableFact]
    public async Task Takeover_joins_the_lobby_for_a_new_guest()
    {
        var lobby = await CreateLobbyAsync();

        var joinRequests = await RunAsync("guest", lobby.Code, null);

        Assert.Equal(1, joinRequests);
        Assert.Equal(2, await SeatsAsync(lobby.Id, null));
        Assert.Equal(1, (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from players p join session_participants s on s.player_id = p.id where s.session_id = @id and p.account_id is null and p.display_name = 'Guest Gwen'",
            new { id = lobby.Id })).Single());
    }

    [SkippableFact]
    public async Task Takeover_with_an_expired_code_shows_the_expired_outcome_and_creates_no_seat()
    {
        var lobby = await CreateLobbyAsync();
        await Host.ExecuteOwnerAsync(
            "update sessions set due_at = now() - interval '1 minute', retry_at = now() + interval '5 minutes' where id = @id",
            new { id = lobby.Id });
        var (cookie, playerId) = await Host.CreateSecondPlayerBrowserSessionAsync();

        var joinRequests = await RunAsync("expired", lobby.Code, cookie);

        Assert.Equal(1, joinRequests);
        Assert.Equal(0, await SeatsAsync(lobby.Id, playerId));
        Assert.Equal(1, await SeatsAsync(lobby.Id, null));
    }

    [SkippableFact]
    public async Task Takeover_completes_when_the_app_start_is_slowed_and_the_join_is_made_once()
    {
        var lobby = await CreateLobbyAsync();
        var (cookie, playerId) = await Host.CreateSecondPlayerBrowserSessionAsync();

        var joinRequests = await RunAsync("slow", lobby.Code, cookie);

        Assert.Equal(1, joinRequests);
        Assert.Equal(1, await SeatsAsync(lobby.Id, playerId));
    }

    private async Task<(Guid Id, string Code)> CreateLobbyAsync()
    {
        var id = Guid.NewGuid();
        var created = await Host.Player.PostAsJsonAsync("/api/sessions", new
        {
            id,
            format = "async_duel",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 4,
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return (id, document.RootElement.GetProperty("code").GetString()!);
    }

    /// <summary>Seats in the session: the player's own, or all of them when no player is named.</summary>
    private async Task<int> SeatsAsync(Guid session, Guid? player) => (await Host.QueryOwnerAsync<int>(
        "select count(*)::int from session_participants where session_id = @session and (@player::uuid is null or player_id = @player)",
        new { session, player })).Single();

    private async Task<int> RunAsync(string scenario, string code, string? playerCookie)
    {
#if QUESSHI_TEST_IDENTITY
        // The identity build reads players from a test header, so the browser's session cookie opens no stream:
        // the app would sign the player out right after the takeover. The normal run covers these scenarios.
        Skip.If(true, "The browser identifies the player by cookie, which the test-identity build does not read.");
#endif
        // Both lookups yield the platform directory that holds the path.
        var scriptRoot = Find(Path.Combine("tests", "browser", "src", "join-takeover-check.mjs"))?.Root;
        var clientRoot = Find(Path.Combine("artifacts", "browser-platform", "wwwroot", "takeover.js"))?.Root;
        var installed = scriptRoot is not null && clientRoot is not null
            && Directory.Exists(Path.Combine(scriptRoot, "tests", "browser", "node_modules", "playwright"));
        // The Platform workflow publishes the client and installs Playwright first, so a missing piece there is a failure.
        Skip.If(!installed && Environment.GetEnvironmentVariable("CI") != "true", "The published client or Playwright is not installed.");
        Assert.True(installed, "The published client (artifacts/browser-platform) or Playwright (tests/browser) is not installed.");

        var start = new ProcessStartInfo("node", Path.Combine(scriptRoot!, "tests", "browser", "src", "join-takeover-check.mjs") + " " + scenario)
        {
            WorkingDirectory = Path.Combine(scriptRoot!, "tests", "browser"),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["PLATFORM_JOIN_HOST"] = Host.TenantOrigin.Host;
        start.Environment["PLATFORM_JOIN_PORT"] = Host.HttpPort.ToString(CultureInfo.InvariantCulture);
        start.Environment["PLATFORM_JOIN_CLIENT_DIR"] = Path.Combine(clientRoot!, "artifacts", "browser-platform", "wwwroot");
        start.Environment["PLATFORM_JOIN_CODE"] = code;
        if (playerCookie is not null) start.Environment["PLATFORM_JOIN_PLAYER_COOKIE"] = playerCookie;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var text = await output;
        Assert.True(process.ExitCode == 0, $"{text}\n{await errors}");
        return JsonDocument.Parse(text.Trim().Split('\n')[^1]).RootElement.GetProperty("joinRequests").GetInt32();
    }

    /// <summary>The nearest ancestor of the test binaries that contains the given path (the platform directory).</summary>
    private static (string Root, string Path)? Find(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return (directory.FullName, candidate);
        }
        return null;
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(_host is null, "QUESSHI_TEST_POSTGRES is not set.");
            return _host!;
        }
    }

    private sealed class TakeoverFeature : IFeature
    {
        public string Id => "join-takeover";
        public FeatureSettings Settings => FeatureSettings.Create(new TakeoverSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record TakeoverSettings;
}
