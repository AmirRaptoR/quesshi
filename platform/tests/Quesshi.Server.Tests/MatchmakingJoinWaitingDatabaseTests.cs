using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Quesshi.Features.Features.Matchmaking;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;
using Quesshi.Server.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class MatchmakingJoinWaitingDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private readonly ScriptedSeating _seating = new();
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new MatchmakingFeature()])
        {
            ConfigureServices = services => services.AddSingleton<ISessionSeating>(provider =>
            {
                _seating.Inner = new GrainSessionSeating(provider.GetRequiredService<IGrainFactory>());
                return _seating;
            }),
        };
        await _host.StartAsync();
        await SeedBankAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task An_async_duel_waiter_plays_their_run_in_their_open_session_before_being_paired()
    {
        using var enqueued = await Enqueue(Host.Player, 10);
        var waiting = Assert.Single(await EntriesAsync());
        var session = Assert.Single(await SessionsAsync());

        var view = await Host.Services.GetRequiredService<IGrainFactory>()
            .GetGrain<ISessionGrain>($"{Host.TenantId}/{session.Id}")
            .ExecuteAsync(new SessionCommand(Host.PlayerId.ToString(), "next", "idle", 0, 0, null, null, null, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.OK, enqueued.StatusCode);
        Assert.Equal((session.Id, "running", "open"), (waiting.SessionId, session.Status, session.CreationShape));
        Assert.Equal(10, await ItemCountAsync(session.Id));
        Assert.Equal([Host.PlayerId], await ParticipantsAsync(session.Id));
        // The owner's run is live in the grain while nobody has joined: a running, seated, deadline-bearing view.
        Assert.True(view.ViewJson is not null, view.Code);
        using var document = System.Text.Json.JsonDocument.Parse(view.ViewJson!);
        Assert.Equal("running", document.RootElement.GetProperty("Status").GetString());
        Assert.Equal(Host.PlayerId.ToString(), document.RootElement.GetProperty("Me").GetProperty("Id").GetString());
        Assert.Equal(session.DueAt, document.RootElement.GetProperty("Deadline").GetDateTimeOffset());
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task A_joiner_gets_the_waiters_questions_and_the_original_deadline_whatever_their_question_count()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        var session = Assert.Single(await SessionsAsync());
        var itemsBefore = await ItemIdsAsync(session.Id);

        using var other = await Enqueue(second, 20);

        var sessions = await SessionsAsync();
        var paired = Assert.Single(sessions, candidate => candidate.Id == session.Id);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equivalent(new[] { Host.PlayerId, secondId }, await ParticipantsAsync(session.Id));
        Assert.Equal(session.DueAt, paired.DueAt);
        Assert.Equal(session.CreatedAt + TimeSpan.FromHours(48), paired.DueAt);
        Assert.Equal(itemsBefore, await ItemIdsAsync(session.Id));
        Assert.Empty(await EntriesAsync());
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task An_entry_is_paired_while_its_owner_is_offline()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);

        using var other = await Enqueue(second, 10);

        // The owner has no stream open and makes no request; the pairing is the joiner's request alone.
        Assert.Equal((HttpStatusCode.OK, 1), (other.StatusCode, _seating.Calls));
        Assert.Equivalent(new[] { Host.PlayerId, secondId }, await ParticipantsAsync((await SessionsAsync())[0].Id));
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task A_failed_grain_call_frees_the_waiting_entry_after_the_lease_and_the_joiners_entry_is_gone()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        var (third, thirdId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        var session = Assert.Single(await SessionsAsync());
        _seating.Before = () => throw new TimeoutException("the grain is unreachable");

        using var failed = await Enqueue(second, 10);
        var afterFailure = await EntriesAsync();
        using var early = await Enqueue(third, 10);
        var duringLease = await EntriesAsync();
        _seating.Before = null;
        await Host.ExecuteOwnerAsync(
            "update matchmaking_entries set claimed_until = now() - interval '1 second' where player_id = @owner",
            new { owner = Host.PlayerId });
        using var retry = await Enqueue(third, 10);

        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("try_again", await ErrorAsync(failed));
        var leased = Assert.Single(afterFailure);
        Assert.Equal(Host.PlayerId, leased.PlayerId);
        Assert.NotNull(leased.ClaimedBy);
        Assert.DoesNotContain(afterFailure, entry => entry.PlayerId == secondId);
        // While the lease runs, the waiting entry is not offered to anyone else.
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Equivalent(new[] { Host.PlayerId, thirdId }, duringLease.Select(entry => entry.PlayerId));
        // After it, a new joiner is paired with it.
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equivalent(new[] { Host.PlayerId, thirdId }, await ParticipantsAsync(session.Id));
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task A_lost_response_from_a_successful_grain_call_completes_the_pairing_with_no_second_opponent()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        var (third, _) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        var session = Assert.Single(await SessionsAsync());
        _seating.After = () => throw new TimeoutException("the response was lost");

        using var paired = await Enqueue(second, 10);
        _seating.After = null;
        using var late = await Enqueue(third, 10);

        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        Assert.Equivalent(new[] { Host.PlayerId, secondId }, await ParticipantsAsync(session.Id));
        var remaining = Assert.Single(await EntriesAsync());
        Assert.NotEqual(Host.PlayerId, remaining.PlayerId);
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task A_pairing_attempt_never_deletes_a_waiting_entry_leased_under_a_different_token()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        var other = Guid.NewGuid();
        _seating.Before = () => Host.ExecuteOwnerAsync(
            "update matchmaking_entries set claimed_by = @other where player_id = @owner",
            new { other, owner = Host.PlayerId });

        using var response = await Enqueue(second, 10);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var kept = Assert.Single(await EntriesAsync());
        Assert.Equal((Host.PlayerId, other), (kept.PlayerId, kept.ClaimedBy));
        Assert.Contains(secondId, await ParticipantsAsync((await SessionsAsync())[0].Id));
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task A_session_that_can_no_longer_be_joined_loses_its_entry_and_the_joiner_is_told_to_try_again()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        var session = Assert.Single(await SessionsAsync());
        await Host.ExecuteOwnerAsync("update sessions set status = 'finished', ended_at = now(), due_at = null, state = '{}' where id = @id", new { id = session.Id });

        using var response = await Enqueue(second, 10);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("try_again", await ErrorAsync(response));
        Assert.DoesNotContain(await EntriesAsync(), entry => entry.PlayerId == Host.PlayerId || entry.PlayerId == secondId);
        Assert.Empty(await EntriesAsync());
        Assert.DoesNotContain(secondId, await ParticipantsAsync(session.Id));
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task The_scheduled_job_removes_entries_whose_session_can_no_longer_be_joined()
    {
        using var first = await Enqueue(Host.Player, 10);
        var session = Assert.Single(await SessionsAsync());
        await Host.ExecuteOwnerAsync("update sessions set due_at = now() - interval '1 minute' where id = @id", new { id = session.Id });

        var entries = await WaitForAsync(EntriesAsync, found => found.Count == 0, TimeSpan.FromSeconds(12));

        Assert.Empty(entries);
        await AssertNoLockOrderViolationAsync();
    }

    [SkippableFact]
    public async Task The_scheduled_job_pairs_two_waiters_who_never_met_into_the_earlier_waiters_session()
    {
        var (second, secondId) = await Host.CreateSecondPlayerClientAsync();
        using var first = await Enqueue(Host.Player, 10);
        var firstSession = Assert.Single(await SessionsAsync());
        // A live lease from someone else keeps the second player from pairing on their own request.
        await Host.ExecuteOwnerAsync(
            "update matchmaking_entries set claimed_by = @token, claimed_until = now() + interval '5 seconds' where player_id = @owner",
            new { token = Guid.NewGuid(), owner = Host.PlayerId });
        using var other = await Enqueue(second, 10);
        Assert.Equal(2, (await EntriesAsync()).Count);
        Assert.Equal(0, _seating.Calls);
        await Host.ExecuteOwnerAsync("update matchmaking_entries set claimed_by = null, claimed_until = null where player_id = @owner", new { owner = Host.PlayerId });

        var entries = await WaitForAsync(EntriesAsync, found => found.Count == 0, TimeSpan.FromSeconds(20));

        Assert.Empty(entries);
        Assert.Equal(1, _seating.Calls);
        // The later waiter joined the earlier waiter's session, and their own open session is not the one played.
        Assert.Equivalent(new[] { Host.PlayerId, secondId }, await ParticipantsAsync(firstSession.Id));
        await AssertNoLockOrderViolationAsync();
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private Task<HttpResponseMessage> Enqueue(HttpClient client, int questionCount) =>
        client.PostAsJsonAsync("/api/x/matchmaking", new { format = "async_duel", settings = new { questionCount } });

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetString();
    }

    private async Task SeedBankAsync()
    {
        var category = Guid.NewGuid();
        await Host.SetCapabilityAsync("format", "async_duel", true);
        await Host.SetCapabilityAsync("source", "bank", true);
        await Host.SetCapabilityAsync("kind", "choice", true);
        await Host.ExecuteOwnerAsync("""
            update tenant_capabilities
            set settings = jsonb_build_object('categoriesByFamily', jsonb_build_object('trivia', jsonb_build_array(@category)))
            where tenant_id = @tenantId and type = 'source' and key = 'bank';
            insert into categories(tenant_id, id, slug, names)
            values (@tenantId, @category, 'duel', '{"en":"Duel"}'::jsonb);
            """, new { tenantId = Host.TenantId, category });
        for (var index = 0; index < 40; index++)
            await Host.ExecuteOwnerAsync("""
                insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
                values (@tenantId, gen_random_uuid(), 'choice', 1, 'trivia', 'en', @category, @level, @prompt,
                        '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, @prompt, 'approved', 'admin')
                """, new { tenantId = Host.TenantId, category, level = index % 3 + 1, prompt = $"{category} {index}" });
    }

    private async Task AssertNoLockOrderViolationAsync()
    {
        var violations = await Host.QueryOwnerAsync<long>("select count(*) from jobs where last_error like '%LockOrderViolation%'", new { });
        Assert.Equal(0, violations.Single());
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            value = await read();
        }
        return value;
    }

    private async Task<IReadOnlyList<SessionRow>> SessionsAsync() => await Host.QueryOwnerAsync<SessionRow>("""
        select id as Id, status as Status, creation_shape as CreationShape, due_at as DueAt, created_at as CreatedAt
        from sessions where tenant_id = @tenantId order by created_at
        """, new { tenantId = Host.TenantId });

    private async Task<IReadOnlyList<Guid>> ParticipantsAsync(Guid sessionId) => await Host.QueryOwnerAsync<Guid>(
        "select player_id from session_participants where tenant_id = @tenantId and session_id = @sessionId and status = 'active'",
        new { tenantId = Host.TenantId, sessionId });

    private async Task<long> ItemCountAsync(Guid sessionId) => (await Host.QueryOwnerAsync<long>(
        "select count(*) from session_items where session_id = @sessionId", new { sessionId })).Single();

    private async Task<IReadOnlyList<string>> ItemIdsAsync(Guid sessionId) => await Host.QueryOwnerAsync<string>(
        "select question_id::text from session_items where session_id = @sessionId order by slot", new { sessionId });

    private async Task<IReadOnlyList<EntryRow>> EntriesAsync() => await Host.QueryOwnerAsync<EntryRow>(
        "select player_id as PlayerId, session_id as SessionId, claimed_by as ClaimedBy from matchmaking_entries where tenant_id = @tenantId order by enqueued_at",
        new { tenantId = Host.TenantId });

    private sealed record SessionRow(Guid Id, string Status, string CreationShape, DateTimeOffset DueAt, DateTimeOffset CreatedAt);

    private sealed record EntryRow(Guid PlayerId, Guid? SessionId, Guid? ClaimedBy);

    /// <summary>The real grain-backed seating, with hooks around it to fail it before or after it succeeds.</summary>
    private sealed class ScriptedSeating : ISessionSeating
    {
        private int _calls;
        public ISessionSeating Inner { get; set; } = null!;
        public Func<Task>? Before { get; set; }
        public Func<Task>? After { get; set; }
        public int Calls => Volatile.Read(ref _calls);

        public async Task<bool> SeatAsync(Guid tenantId, Guid sessionId, Guid playerId, Guid pairingToken, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (Before is { } before) await before();
            var seated = await Inner.SeatAsync(tenantId, sessionId, playerId, pairingToken, cancellationToken);
            if (After is { } after) await after();
            return seated;
        }
    }
}
