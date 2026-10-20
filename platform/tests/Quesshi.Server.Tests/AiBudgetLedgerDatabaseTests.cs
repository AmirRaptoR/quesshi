using Dapper;
using Npgsql;
using Quesshi.Features.AiGeneration;
using Quesshi.Infrastructure.Database;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AiBudgetLedgerDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Reservation_that_exceeds_the_monthly_limit_is_refused_before_sending()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);

        await test.Ledger.ReserveAsync(test.TenantId, Request(Guid.NewGuid(), 0.75m));
        var rejectedCall = Guid.NewGuid();

        Assert.Equal(AiReserveResult.BudgetExceeded, await test.Ledger.ReserveAsync(test.TenantId, Request(rejectedCall, 0.5m)));
        Assert.False(await test.Ledger.TrySendAsync(test.TenantId, rejectedCall));
    }

    [SkippableFact]
    public async Task Racing_workers_receive_exactly_one_permission_to_send_a_reserved_call()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));

        var sends = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => test.Ledger.TrySendAsync(test.TenantId, callId)));

        Assert.Equal(1, sends.Count(sent => sent));
    }

    [SkippableFact]
    public async Task Settling_a_sent_call_releases_its_bound_and_records_actual_cost_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));
        await test.Ledger.TrySendAsync(test.TenantId, callId);
        await test.Ledger.SettleAsync(test.TenantId, callId, new AiUsage(10, 20, 0.3m));
        await test.Ledger.SettleAsync(test.TenantId, callId, new AiUsage(10, 20, 0.3m));

        Assert.Equal(new CallBudgetState("done", 0m, 0.3m), await test.ReadStateAsync(callId, "2026-10"));
    }

    [SkippableFact]
    public async Task Recovery_expires_a_fifteen_minute_unsent_reservation_and_releases_its_bound()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));
        test.Time.Advance(TimeSpan.FromMinutes(15));

        await test.Ledger.RecoverAsync(test.TenantId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        Assert.Equal(new CallBudgetState("expired", 0m, 0m), await test.ReadStateAsync(callId, "2026-10"));
    }

    [SkippableFact]
    public async Task Recovery_marks_an_overdue_sent_call_unknown_and_counts_its_bound_as_spent()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));
        await test.Ledger.TrySendAsync(test.TenantId, callId);
        test.Time.Advance(TimeSpan.FromSeconds(36));

        await test.Ledger.RecoverAsync(test.TenantId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        Assert.Equal(new CallBudgetState("unknown", 0m, 0.5m), await test.ReadStateAsync(callId, "2026-10"));
    }

    [SkippableFact]
    public async Task Unknown_calls_never_receive_a_second_send_permission()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));
        await test.Ledger.TrySendAsync(test.TenantId, callId);
        test.Time.Advance(TimeSpan.FromSeconds(36));
        await test.Ledger.RecoverAsync(test.TenantId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        Assert.False(await test.Ledger.TrySendAsync(test.TenantId, callId));
    }

    [SkippableFact]
    public async Task A_late_result_replaces_an_unknown_calls_spent_bound_with_actual_cost_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));
        await test.Ledger.TrySendAsync(test.TenantId, callId);
        test.Time.Advance(TimeSpan.FromSeconds(36));
        await test.Ledger.RecoverAsync(test.TenantId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        await test.Ledger.SettleAsync(test.TenantId, callId, new AiUsage(10, 20, 0.2m));
        await test.Ledger.SettleAsync(test.TenantId, callId, new AiUsage(10, 20, 0.2m));

        Assert.Equal(new CallBudgetState("done", 0m, 0.2m), await test.ReadStateAsync(callId, "2026-10"));
    }

    [SkippableFact]
    public async Task Settlement_updates_the_month_recorded_on_the_call_after_calendar_rollover()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.Zero));
        var callId = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(callId, 0.5m));
        await test.Ledger.TrySendAsync(test.TenantId, callId);
        test.Time.Advance(TimeSpan.FromMinutes(2));
        await test.Ledger.SettleAsync(test.TenantId, callId, new AiUsage(10, 20, 0.2m));

        Assert.Equal(new CallBudgetState("done", 0m, 0.2m), await test.ReadStateAsync(callId, "2026-10"));
    }

    [SkippableFact]
    public async Task Every_transition_leaves_nonnegative_postgresql_budget_balances()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var test = await LedgerTest.CreateAsync(fixture, 1m, October);
        var settled = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        await test.Ledger.ReserveAsync(test.TenantId, Request(settled, 0.25m));
        Assert.True((await test.ReadBudgetAsync("2026-10")).IsNonnegative);
        await test.Ledger.TrySendAsync(test.TenantId, settled);
        Assert.True((await test.ReadBudgetAsync("2026-10")).IsNonnegative);
        await test.Ledger.SettleAsync(test.TenantId, settled, new AiUsage(1, 1, 0.1m));
        Assert.True((await test.ReadBudgetAsync("2026-10")).IsNonnegative);
        await test.Ledger.ReserveAsync(test.TenantId, Request(expired, 0.25m));
        await test.Ledger.ReserveAsync(test.TenantId, Request(unknown, 0.25m));
        Assert.True((await test.ReadBudgetAsync("2026-10")).IsNonnegative);
        await test.Ledger.TrySendAsync(test.TenantId, unknown);
        Assert.True((await test.ReadBudgetAsync("2026-10")).IsNonnegative);
        test.Time.Advance(TimeSpan.FromMinutes(15));
        await test.Ledger.RecoverAsync(test.TenantId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        Assert.True((await test.ReadBudgetAsync("2026-10")).IsNonnegative);
    }

    [SkippableFact]
    public async Task A_tenant_cannot_send_another_tenants_reserved_call()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var first = await LedgerTest.CreateAsync(fixture, 1m, October);
        await using var second = await LedgerTest.CreateAsync(fixture, 1m, October);
        var callId = Guid.NewGuid();
        await first.Ledger.ReserveAsync(first.TenantId, Request(callId, 0.5m));

        Assert.False(await second.Ledger.TrySendAsync(second.TenantId, callId));
    }

    private static readonly DateTimeOffset October = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static AiProviderRequest Request(Guid callId, decimal bound)
        => new(callId, null, "generate", "test-model", 0, 1, 0m, bound);

    private sealed record CallBudgetState(string Status, decimal ReservedUsd, decimal SpentUsd);

    private sealed record BudgetState(decimal ReservedUsd, decimal SpentUsd)
    {
        public bool IsNonnegative => ReservedUsd >= 0m && SpentUsd >= 0m;
    }

    private sealed class LedgerTest : IAsyncDisposable
    {
        private readonly DatabaseSources sources;
        private readonly string ownerConnectionString;

        private LedgerTest(Guid tenantId, DatabaseSources sources, string ownerConnectionString, MutableTimeProvider time)
        {
            TenantId = tenantId;
            this.sources = sources;
            this.ownerConnectionString = ownerConnectionString;
            Time = time;
            Ledger = new BudgetLedger(new TenantDb(sources), time);
        }

        public Guid TenantId { get; }
        public BudgetLedger Ledger { get; }
        public MutableTimeProvider Time { get; }

        public static async Task<LedgerTest> CreateAsync(PostgresFixture fixture, decimal limit, DateTimeOffset now)
        {
            var ownerConnectionString = fixture.ConnectionStringForRole("quesshi_owner");
            await Program.MigrateAsync(ownerConnectionString);
            var tenantId = Guid.NewGuid();
            await using var owner = new NpgsqlConnection(ownerConnectionString);
            await owner.OpenAsync();
            await owner.ExecuteAsync("insert into tenants (id, slug, name, default_language) values (@tenantId, @slug, 'AI ledger', 'en')", new { tenantId, slug = $"ai-ledger-{tenantId:N}" });
            await owner.ExecuteAsync("insert into ai_budgets (tenant_id, month, limit_usd) values (@tenantId, '2026-10', @limit)", new { tenantId, limit });
            var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
            return new LedgerTest(tenantId, sources, ownerConnectionString, new MutableTimeProvider(now));
        }

        public async Task<CallBudgetState> ReadStateAsync(Guid callId, string month)
        {
            await using var owner = new NpgsqlConnection(ownerConnectionString);
            return await owner.QuerySingleAsync<CallBudgetState>("""
                select c.status as Status, b.reserved_usd as ReservedUsd, b.spent_usd as SpentUsd
                from ai_calls c join ai_budgets b on b.tenant_id = c.tenant_id and b.month = @month
                where c.tenant_id = @tenantId and c.id = @callId
                """, new { tenantId = TenantId, callId, month });
        }

        public async Task<BudgetState> ReadBudgetAsync(string month)
        {
            await using var owner = new NpgsqlConnection(ownerConnectionString);
            return await owner.QuerySingleAsync<BudgetState>("""
                select reserved_usd as ReservedUsd, spent_usd as SpentUsd
                from ai_budgets where tenant_id = @tenantId and month = @month
                """, new { tenantId = TenantId, month });
        }

        public ValueTask DisposeAsync() => sources.DisposeAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
