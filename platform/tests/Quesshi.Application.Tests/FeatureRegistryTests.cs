using Quesshi.Application.Jobs;
using Quesshi.Plugin.Api;
using System.Text.Json;

namespace Quesshi.Application.Tests;

public sealed class FeatureRegistryTests
{
    [Fact]
    public void Registering_a_feature_exposes_typed_settings_and_allows_one_declaration_of_each_kind()
    {
        var capabilities = new CapabilityRegistry();
        var jobs = new JobHandlerRegistry();
        var schedules = new JobScheduleRegistry(jobs);
        var features = new FeatureRegistry(capabilities, jobs, schedules);

        features.Register(new SampleFeature());

        var settings = Assert.IsType<FeatureCapabilityDefinition>(capabilities.Get(CapabilityType.Feature, "sample"));
        Assert.Equal(typeof(SampleSettings), settings.SettingsType);
        Assert.Equal(new SampleSettings("default"), settings.DefaultSettingsObject);
        Assert.Empty(settings.ValidateObject(new SampleSettings("valid")));
        Assert.Equal(["Name is required."], settings.ValidateObject(new SampleSettings("")));
        Assert.True(jobs.TryGet("sample", "refresh", out var job));
        Assert.Equal(WhenDisabled.Cancel, job.WhenDisabled);
        Assert.Collection(schedules.Schedules, schedule => Assert.Equal(("sample", "refresh"), (schedule.Owner, schedule.Kind)));
        Assert.Contains(("sample", typeof(SampleEvent)), features.EventHandlers.Keys);
        Assert.Contains("sample", features.CleanupHandlers.Keys);
    }

    [Fact]
    public void Retired_feature_keeps_jobs_and_cleanup_but_suppresses_events_and_schedules()
    {
        var capabilities = new CapabilityRegistry();
        var jobs = new JobHandlerRegistry();
        var schedules = new JobScheduleRegistry(jobs);
        var features = new FeatureRegistry(capabilities, jobs, schedules);

        features.Register(new SampleFeature(retired: true));

        Assert.NotNull(capabilities.TryGet(CapabilityType.Feature, "sample"));
        Assert.True(jobs.TryGet("sample", "refresh", out _));
        Assert.Empty(schedules.Schedules);
        Assert.Empty(features.EventHandlers);
        Assert.Contains("sample", features.CleanupHandlers.Keys);
    }

    [Fact]
    public void Active_features_expose_player_and_open_route_contributions_but_retired_features_do_not()
    {
        var active = CreateRegistry();
        active.Register(new RouteFeature());

        var routes = Assert.Single(active.Routes);
        Assert.Equal("routes", routes.Id);
        Assert.Single(routes.Player);
        Assert.Single(routes.Open);

        var retired = CreateRegistry();
        retired.Register(new RouteFeature(retired: true));
        Assert.Empty(retired.Routes);
    }

    [Fact]
    public void Duplicate_feature_declarations_fail_deterministically()
    {
        var registry = CreateRegistry();
        registry.Register(new SampleFeature());
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SampleFeature()));

        Assert.Throws<InvalidOperationException>(() => CreateRegistry().Register(new DuplicateEventFeature()));
        Assert.Throws<InvalidOperationException>(() => CreateRegistry().Register(new DuplicateCleanupFeature()));
        Assert.Throws<InvalidOperationException>(() => CreateRegistry().Register(new DuplicateJobFeature()));
    }

    [Fact]
    public void Invalid_default_settings_are_rejected_before_registration()
    {
        var registry = CreateRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.Register(new InvalidSettingsFeature()));
    }

    [Fact]
    public async Task Declared_jobs_preserve_complete_and_cancel_policies_when_disabled_or_retired()
    {
        var capabilities = new CapabilityRegistry();
        var handlers = new JobHandlerRegistry();
        var features = new FeatureRegistry(capabilities, handlers, new JobScheduleRegistry(handlers));
        var completedHandlerRan = false;
        features.Register(new WorkFeature(retired: true, () => completedHandlerRan = true));
        var tenantId = Guid.NewGuid();
        var store = new RecordingJobStore([
            Lease(1, tenantId, "work", "complete"),
            Lease(2, tenantId, "work", "cancel")]);

        var worker = new JobWorker(store, handlers, new UnusedTenantDb(), new DisabledCapabilities());
        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));

        Assert.True(completedHandlerRan);
        Assert.Equal([1], store.Completed);
        Assert.Equal([2], store.Cancelled);
    }

    [Fact]
    public async Task Declared_schedule_targets_enabled_tenants_once_per_window()
    {
        var capabilities = new CapabilityRegistry();
        var handlers = new JobHandlerRegistry();
        var schedules = new JobScheduleRegistry(handlers);
        var features = new FeatureRegistry(capabilities, handlers, schedules);
        features.Register(new SampleFeature());
        var jobs = new RecordedSchedules();
        var scheduler = new JobScheduler(schedules, new OneEnabledTenant(), jobs,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-10-13T12:00:30Z")));

        Assert.Equal(1, await scheduler.RunPassAsync());
        Assert.Equal(0, await scheduler.RunPassAsync());
        var scheduled = Assert.Single(jobs.Scheduled);
        Assert.Equal(("sample", "refresh"), (scheduled.Schedule.Owner, scheduled.Schedule.Kind));
    }

    [Fact]
    public async Task Registered_inbox_handler_contributes_items_for_the_requested_player()
    {
        var registry = CreateRegistry();
        registry.Register(new InboxFeature());
        var transaction = new QueryTransaction(enabled: true);
        var playerId = Guid.NewGuid();

        var items = await registry.GetInboxItemsAsync(transaction, playerId, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(("sample.notice", playerId), (item.Type, item.Payload.GetProperty("playerId").GetGuid()));
    }

    [Fact]
    public async Task Declared_consumer_invokes_an_enabled_typed_named_query_in_its_transaction()
    {
        var registry = CreateRegistry();
        var provider = new QueryProviderFeature();
        registry.Register(provider);
        registry.Register(new QueryConsumerFeature());
        var transaction = new QueryTransaction(enabled: true);
        var playerId = Guid.NewGuid();

        var result = await registry.InvokeQueryAsync<QueryValue>("consumer", "provider.value", transaction, playerId, CancellationToken.None);

        Assert.Equal(
            (new NamedQueryResult<QueryValue>(true, new QueryValue(playerId)), (ITenantTx?)transaction),
            (result, provider.ReceivedTransaction));
    }

    [Fact]
    public async Task Declared_consumer_receives_an_unavailable_result_when_the_provider_is_disabled()
    {
        var registry = CreateRegistry();
        registry.Register(new QueryProviderFeature());
        registry.Register(new QueryConsumerFeature());

        var result = await registry.InvokeQueryAsync<QueryValue>("consumer", "provider.value", new QueryTransaction(enabled: false), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(NamedQueryResult<QueryValue>.Unavailable, result);
    }

    [Fact]
    public async Task Declared_consumer_receives_an_unavailable_result_when_no_provider_is_registered()
    {
        var registry = CreateRegistry();
        registry.Register(new QueryConsumerFeature());

        var result = await registry.InvokeQueryAsync<QueryValue>("consumer", "provider.value", new QueryTransaction(enabled: true), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(NamedQueryResult<QueryValue>.Unavailable, result);
    }

    private static FeatureRegistry CreateRegistry()
    {
        var jobs = new JobHandlerRegistry();
        return new FeatureRegistry(new CapabilityRegistry(), jobs, new JobScheduleRegistry(jobs));
    }

    private sealed record SampleSettings(string Name);

    private sealed record SampleEvent;

    private sealed class SampleFeature(bool retired = false) : IFeature
    {
        public string Id => "sample";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), settings =>
            string.IsNullOrWhiteSpace(settings.Name) ? ["Name is required."] : []);
        public bool Retired => retired;

        public void Configure(IFeatureBuilder feature)
        {
            feature.On<SampleEvent>((_, _, _) => Task.CompletedTask);
            feature.CleanupPlayer((_, _, _) => Task.CompletedTask);
            feature.Every(TimeSpan.FromMinutes(1), "refresh");
            feature.Job("refresh", (_, _) => Task.CompletedTask, WhenDisabled.Cancel);
        }
    }

    private sealed class DuplicateEventFeature : IFeature
    {
        public string Id => "events";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);
        public void Configure(IFeatureBuilder feature)
        {
            feature.On<SampleEvent>((_, _, _) => Task.CompletedTask);
            feature.On<SampleEvent>((_, _, _) => Task.CompletedTask);
        }
    }

    private sealed class DuplicateCleanupFeature : IFeature
    {
        public string Id => "cleanup";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);
        public void Configure(IFeatureBuilder feature)
        {
            feature.CleanupPlayer((_, _, _) => Task.CompletedTask);
            feature.CleanupPlayer((_, _, _) => Task.CompletedTask);
        }
    }

    private sealed class DuplicateJobFeature : IFeature
    {
        public string Id => "jobs";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);
        public void Configure(IFeatureBuilder feature)
        {
            feature.Job("same", (_, _) => Task.CompletedTask);
            feature.Job("same", (_, _) => Task.CompletedTask);
        }
    }

    private sealed class InvalidSettingsFeature : IFeature
    {
        public string Id => "invalid";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings(""), _ => ["invalid"]);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed class WorkFeature(bool retired, Action complete) : IFeature
    {
        public string Id => "work";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);
        public bool Retired => retired;
        public void Configure(IFeatureBuilder feature)
        {
            feature.Job("complete", (_, _) => { complete(); return Task.CompletedTask; });
            feature.Job("cancel", (_, _) => Task.CompletedTask, WhenDisabled.Cancel);
        }
    }

    private sealed class RouteFeature(bool retired = false) : IFeature
    {
        public string Id => "routes";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);
        public bool Retired => retired;

        public void Configure(IFeatureBuilder feature)
        {
            feature.PlayerRoutes(_ => { });
            feature.OpenRoutes(_ => { });
        }
    }

    private sealed class InboxFeature : IFeature
    {
        public string Id => "inbox";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);

        public void Configure(IFeatureBuilder feature) => feature.Inbox(async (_, playerId, cancellationToken) =>
        {
            await Task.CompletedTask.WaitAsync(cancellationToken);
            return [new InboxItem("sample.notice", JsonSerializer.SerializeToElement(new { playerId }))];
        });
    }

    private sealed record QueryValue(Guid PlayerId);

    private sealed class QueryProviderFeature : IFeature
    {
        public string Id => "provider";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);

        public ITenantTx? ReceivedTransaction { get; private set; }

        public void Configure(IFeatureBuilder feature) => feature.Provides<QueryValue>("provider.value", (transaction, playerId, cancellationToken) =>
        {
            ReceivedTransaction = transaction;
            return Task.FromResult(new QueryValue(playerId));
        });
    }

    private sealed class QueryConsumerFeature : IFeature
    {
        public string Id => "consumer";
        public FeatureSettings Settings => FeatureSettings.Create(new SampleSettings("default"), _ => []);

        public void Configure(IFeatureBuilder feature) => feature.Uses("provider.value");
    }

    private sealed class QueryTransaction(bool enabled) : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>([]);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(typeof(T) == typeof(bool?) ? (T?)(object?)(bool?)enabled : default);
    }

    private static LeasedJob Lease(long id, Guid tenantId, string owner, string kind) =>
        new(id, tenantId, owner, kind, JsonSerializer.SerializeToElement(new { }), Guid.NewGuid(), DateTimeOffset.UtcNow, 1, null);

    private sealed class RecordingJobStore(IEnumerable<LeasedJob> jobs) : IJobStore
    {
        private readonly Queue<LeasedJob> _jobs = new(jobs);
        public List<long> Completed { get; } = [];
        public List<long> Cancelled { get; } = [];
        public Task<IReadOnlyList<LeasedJob>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LeasedJob>>(_jobs.Take(batchSize).ToArray());
        public Task<bool> CompleteAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) { Completed.Add(id); return Task.FromResult(true); }
        public Task<bool> CancelAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) { Cancelled.Add(id); return Task.FromResult(true); }
        public Task<bool> FailAsync(long id, Guid lockedBy, int attempts, int? maxAttempts, string error, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<DeadJob>> ListDeadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeadJob>>([]);
        public Task<bool> RerunDeadAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class UnusedTenantDb : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) =>
            throw new NotSupportedException();
    }

    private sealed class DisabledCapabilities : ICapabilities
    {
        public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
        public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
    }

    private sealed class OneEnabledTenant : IJobScheduleTenants
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        public Task<IReadOnlyList<Guid>> ListEnabledAsync(string owner, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>>([_tenantId]);
    }

    private sealed class RecordedSchedules : ISystemScheduledJobs
    {
        private readonly HashSet<string> _uniqueKeys = [];
        public List<(JobSchedule Schedule, string UniqueKey)> Scheduled { get; } = [];
        public Task<bool> EnqueueOnceAsync(Guid tenantId, JobSchedule schedule, JsonElement payload, string uniqueKey, DateTimeOffset window, CancellationToken cancellationToken = default)
        {
            if (!_uniqueKeys.Add(uniqueKey)) return Task.FromResult(false);
            Scheduled.Add((schedule, uniqueKey));
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
