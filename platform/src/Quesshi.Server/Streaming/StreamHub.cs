using System.Collections.Concurrent;
using System.Threading.Channels;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Streaming;

internal enum StreamMessageKind { View, Config, Gone }

internal sealed record StreamMessage(StreamMessageKind Kind, long Version, string? Json = null, string? Reason = null);

/// <summary>One bounded local stream subscription. Delivery is always convergence-oriented.</summary>
internal sealed class StreamSubscription(Guid tenantId, string playerId, Guid? sessionId)
{
    private readonly object _gate = new();
    private readonly Channel<StreamMessage> _messages = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    public Guid TenantId { get; } = tenantId;
    public string PlayerId { get; } = playerId;
    public Guid? SessionId { get; } = sessionId;
    public ChannelReader<StreamMessage> Messages => _messages.Reader;
    public long LastSentVersion { get; private set; } = -1;
    public long LastQueuedVersion { get; private set; } = -1;
    public long LastConfigVersion { get; private set; }

    public void QueueView(RenderedSessionView view)
    {
        lock (_gate)
        {
            if (view.Version <= Math.Max(LastSentVersion, LastQueuedVersion)) return;
            LastQueuedVersion = view.Version;
            _messages.Writer.TryWrite(new StreamMessage(StreamMessageKind.View, view.Version, view.Json));
        }
    }

    public void QueueConfig(long version)
    {
        lock (_gate)
        {
            // Preserve the convergence-critical view already in the one-slot buffer. The
            // periodic reconcile will retry this config after that view has been sent.
            if (version <= LastConfigVersion || LastQueuedVersion > LastSentVersion) return;
            _messages.Writer.TryWrite(new StreamMessage(StreamMessageKind.Config, version));
        }
    }

    public void QueueGone(string reason)
    {
        lock (_gate) _messages.Writer.TryWrite(new StreamMessage(StreamMessageKind.Gone, 0, Reason: reason));
    }

    public bool MarkSent(StreamMessage message)
    {
        lock (_gate)
        {
            if (message.Kind == StreamMessageKind.View)
            {
                if (message.Version <= LastSentVersion) return false;
                LastSentVersion = message.Version;
                return true;
            }
            if (message.Kind == StreamMessageKind.Config)
            {
                if (message.Version <= LastConfigVersion) return false;
                LastConfigVersion = message.Version;
            }
            return true;
        }
    }

    public bool NeedsView(long version)
    {
        lock (_gate) return version > Math.Max(LastSentVersion, LastQueuedVersion);
    }

    public void Complete()
    {
        lock (_gate) _messages.Writer.TryComplete();
    }
}

/// <summary>
/// Per-silo stream coordinator. It performs one snapshot/shared render for each watched session
/// and only keeps the newest personal view per client.
/// </summary>
internal sealed class StreamHub : BackgroundService
{
    private readonly ISessionSnapshotLoader snapshots;
    private readonly IViewRenderer renderer;
    private readonly IFormatRegistry formats;
    private readonly ITenantDb database;
    private readonly IStreamNotificationSource notifications;
    private readonly IClock clock;
    private readonly ILogger<StreamHub> logger;
    private readonly ConcurrentDictionary<Guid, StreamSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), SessionGate> _sessionGates = new();
    private readonly object _sessionGateLock = new();
    private readonly SemaphoreSlim _renderJobs = new(8, 8);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), long> _pendingVersions = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), byte> _scheduledRenders = new();
    private int _disposed;

    public StreamHub(ISessionSnapshotLoader snapshots, IViewRenderer renderer, IFormatRegistry formats, ITenantDb database,
        IStreamNotificationSource notifications, IClock clock, ILogger<StreamHub> logger)
    {
        this.snapshots = snapshots;
        this.renderer = renderer;
        this.formats = formats;
        this.database = database;
        this.notifications = notifications;
        this.clock = clock;
        this.logger = logger;
        notifications.Received += OnNotification;
    }

    public StreamSubscription Subscribe(Guid tenantId, string playerId, Guid? sessionId)
    {
        var subscription = new StreamSubscription(tenantId, playerId, sessionId);
        _subscriptions.TryAdd(Guid.NewGuid(), subscription);
        return subscription;
    }

    internal int SessionGateCount => _sessionGates.Count;

    public void Unsubscribe(StreamSubscription subscription)
    {
        var entry = _subscriptions.FirstOrDefault(pair => ReferenceEquals(pair.Value, subscription));
        if (entry.Key != Guid.Empty) _subscriptions.TryRemove(entry.Key, out _);
        subscription.Complete();
        if (subscription.SessionId is { } sessionId) RemoveGateIfUnwatched((subscription.TenantId, sessionId));
    }

    public Task RenderInitialAsync(StreamSubscription subscription, CancellationToken cancellationToken) =>
        subscription.SessionId is { } sessionId ? RenderSessionAsync(subscription.TenantId, sessionId, cancellationToken) : Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileAsync(stoppingToken);
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ReconcileCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to reconcile stream subscriptions; retrying on the next interval.");
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var subscriptions = _subscriptions.Values.ToArray();
        foreach (var tenant in subscriptions.GroupBy(subscription => subscription.TenantId))
        {
            var watched = tenant.Where(subscription => subscription.SessionId is not null)
                .Select(subscription => subscription.SessionId!.Value).Distinct().ToArray();
            var state = await database.InTenantAsync(tenant.Key, async transaction =>
            {
                var sessionVersions = watched.Length == 0
                    ? []
                    : await transaction.QueryAsync<SessionVersion>("""
                        select id as SessionId, version as Version
                        from sessions
                        where id = any(@sessionIds)
                        """, new { sessionIds = watched }, cancellationToken);
                var configVersion = await transaction.QuerySingleOrDefaultAsync<long?>(
                    "select config_version from tenants where id = @tenantId", new { tenantId = tenant.Key }, cancellationToken);
                return new ReconciliationState(sessionVersions, configVersion);
            }, cancellationToken: cancellationToken, readOnly: true);

            var versions = state.SessionVersions.ToDictionary(version => version.SessionId);
            foreach (var sessionId in watched)
            {
                var targets = tenant.Where(subscription => subscription.SessionId == sessionId).ToArray();
                if (!versions.TryGetValue(sessionId, out var version) || targets.Any(target => target.NeedsView(version.Version)))
                {
                    await RenderSessionAsync(tenant.Key, sessionId, cancellationToken);
                }
            }
            if (state.ConfigVersion is { } current) BroadcastConfig(tenant.Key, current);
        }
    }

    private sealed record SessionVersion(Guid SessionId, long Version);
    private sealed record ReconciliationState(IReadOnlyList<SessionVersion> SessionVersions, long? ConfigVersion);

    private void OnNotification(StreamNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        switch (notification)
        {
            case StreamNotification.Session session:
                var key = (session.TenantId, session.SessionId);
                _pendingVersions.AddOrUpdate(key, session.Version, (_, existing) => Math.Max(existing, session.Version));
                if (_scheduledRenders.TryAdd(key, 0)) _ = CoalescedRenderAsync(session.TenantId, session.SessionId);
                break;
            case StreamNotification.Config config:
                BroadcastConfig(config.TenantId, config.Version);
                break;
            case StreamNotification.Reconnected:
                _ = ReconcileAsync();
                break;
        }
    }

    private async Task CoalescedRenderAsync(Guid tenantId, Guid sessionId)
    {
        var key = (tenantId, sessionId);
        try
        {
            // A single scheduled render absorbs bursts, then leaves at least 250ms before a
            // newly-arrived version can produce another delivery.
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                _pendingVersions.TryRemove(key, out _);
                await RenderSessionAsync(tenantId, sessionId, CancellationToken.None);
            }
            while (_pendingVersions.ContainsKey(key));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to render stream session {SessionId}", sessionId);
        }
        finally
        {
            _scheduledRenders.TryRemove(key, out _);
            if (_pendingVersions.ContainsKey(key) && _scheduledRenders.TryAdd(key, 0)) _ = CoalescedRenderAsync(tenantId, sessionId);
        }
    }

    private async Task RenderSessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        var targets = _subscriptions.Values.Where(subscription => subscription.TenantId == tenantId && subscription.SessionId == sessionId).ToArray();
        if (targets.Length == 0) return;

        var key = (tenantId, sessionId);
        var gate = AcquireSessionGate(key);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            await _renderJobs.WaitAsync(cancellationToken);
            try
            {
                // The loader completes its read transaction before this method queues any stream data.
                var state = await snapshots.LoadAsync(tenantId, sessionId, cancellationToken);
                if (state is null)
                {
                    foreach (var target in targets) target.QueueGone("not_found");
                    return;
                }
                if (!targets.Any(target => target.NeedsView(state.Session.Version))) return;
                if (!formats.TryGet(state.Session.Format ?? "", out var format) || format is null)
                {
                    logger.LogWarning("No format {Format} registered for stream session {SessionId}", state.Session.Format, sessionId);
                    foreach (var target in targets) target.QueueGone("not_found");
                    return;
                }

                var shared = renderer.Snapshot(state, clock.Now);
                foreach (var target in targets)
                {
                    var view = renderer.Render(shared, format, target.PlayerId);
                    if (view is null) target.QueueGone("not_found");
                    else target.QueueView(view);
                }
            }
            finally { _renderJobs.Release(); }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseSessionGate(key, gate);
        }
    }

    private sealed class SessionGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private SessionGate AcquireSessionGate((Guid Tenant, Guid Session) key)
    {
        lock (_sessionGateLock)
        {
            var gate = _sessionGates.GetOrAdd(key, _ => new SessionGate());
            gate.Users++;
            return gate;
        }
    }

    private void ReleaseSessionGate((Guid Tenant, Guid Session) key, SessionGate gate)
    {
        lock (_sessionGateLock)
        {
            gate.Users--;
            if (gate.Users == 0 && !IsWatched(key)) _sessionGates.TryRemove(key, out _);
        }
    }

    private void RemoveGateIfUnwatched((Guid Tenant, Guid Session) key)
    {
        lock (_sessionGateLock)
        {
            if (_sessionGates.TryGetValue(key, out var gate) && gate.Users == 0 && !IsWatched(key)) _sessionGates.TryRemove(key, out _);
        }
    }

    private bool IsWatched((Guid Tenant, Guid Session) key) => _subscriptions.Values.Any(subscription =>
        subscription.TenantId == key.Tenant && subscription.SessionId == key.Session);

    private void BroadcastConfig(Guid tenantId, long version)
    {
        foreach (var subscription in _subscriptions.Values.Where(subscription => subscription.TenantId == tenantId)) subscription.QueueConfig(version);
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        notifications.Received -= OnNotification;
        foreach (var subscription in _subscriptions.Values) subscription.Complete();
        base.Dispose();
    }
}
