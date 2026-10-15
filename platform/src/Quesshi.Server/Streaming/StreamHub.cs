using System.Collections.Concurrent;
using System.Threading.Channels;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server.Accounts;

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
    private bool _terminal;

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
            if (_terminal || view.Version <= Math.Max(LastSentVersion, LastQueuedVersion)) return;
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
            if (_terminal || version <= LastConfigVersion || LastQueuedVersion > LastSentVersion) return;
            _messages.Writer.TryWrite(new StreamMessage(StreamMessageKind.Config, version));
        }
    }

    public void QueueGone(string reason)
    {
        lock (_gate)
        {
            if (_terminal) return;
            _terminal = true;
            _messages.Writer.TryWrite(new StreamMessage(StreamMessageKind.Gone, 0, Reason: reason));
            _messages.Writer.TryComplete();
        }
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
        lock (_gate) return !_terminal && version > Math.Max(LastSentVersion, LastQueuedVersion);
    }

    public void Complete()
    {
        lock (_gate)
        {
            _terminal = true;
            _messages.Writer.TryComplete();
        }
    }
}

/// <summary>
/// Per-silo stream coordinator. It performs one snapshot/shared render for each watched session
/// and only keeps the newest personal view per client.
/// </summary>
internal sealed class StreamHub : BackgroundService, IAccountDeletionNotifier
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
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), SessionRenderSchedule> _renderSchedules = new();
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
    internal int SubscriptionCount => _subscriptions.Count;

    public void Unsubscribe(StreamSubscription subscription)
    {
        var entry = _subscriptions.FirstOrDefault(pair => ReferenceEquals(pair.Value, subscription));
        if (entry.Key != Guid.Empty) _subscriptions.TryRemove(entry.Key, out _);
        subscription.Complete();
        if (subscription.SessionId is { } sessionId) RemoveGateIfUnwatched((subscription.TenantId, sessionId));
    }

    public void Deleted(IReadOnlyList<AccountDeletionPlayer> players)
    {
        foreach (var player in players)
        {
            foreach (var subscription in _subscriptions.Values.Where(subscription =>
                         subscription.TenantId == player.TenantId
                         && Guid.TryParse(subscription.PlayerId, out var playerId)
                         && playerId == player.PlayerId))
            {
                subscription.QueueGone("revoked");
            }
        }
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
            var players = tenant.Select(subscription => Guid.TryParse(subscription.PlayerId, out var playerId) ? playerId : (Guid?)null)
                .Where(playerId => playerId is not null).Select(playerId => playerId!.Value).Distinct().ToArray();
            var state = await database.InTenantAsync(tenant.Key, async transaction =>
            {
                var sessionVersions = watched.Length == 0
                    ? []
                    : await transaction.QueryAsync<SessionVersion>("""
                        select id as SessionId, version as Version
                        from sessions
                        where id = any(@sessionIds)
                        """, new { sessionIds = watched }, cancellationToken);
                var livePlayers = players.Length == 0
                    ? []
                    : await transaction.QueryAsync<Guid>("""
                        select id
                        from players
                        where id = any(@playerIds) and deleted_at is null
                        """, new { playerIds = players }, cancellationToken);
                var configVersion = await transaction.QuerySingleOrDefaultAsync<long?>(
                    "select config_version from tenants where id = @tenantId", new { tenantId = tenant.Key }, cancellationToken);
                return new ReconciliationState(sessionVersions, livePlayers, configVersion);
            }, cancellationToken: cancellationToken, readOnly: true);

            var versions = state.SessionVersions.ToDictionary(version => version.SessionId);
            var livePlayerIds = state.LivePlayers.ToHashSet();
            foreach (var subscription in tenant.Where(subscription =>
                         Guid.TryParse(subscription.PlayerId, out var playerId) && !livePlayerIds.Contains(playerId)))
            {
                subscription.QueueGone("revoked");
            }
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

    internal sealed record SessionVersion(Guid SessionId, long Version);
    private sealed record ReconciliationState(IReadOnlyList<SessionVersion> SessionVersions, IReadOnlyList<Guid> LivePlayers, long? ConfigVersion);

    private void OnNotification(StreamNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        switch (notification)
        {
            case StreamNotification.Session session:
                var key = (session.TenantId, session.SessionId);
                var schedule = _renderSchedules.GetOrAdd(key, _ => new SessionRenderSchedule());
                if (schedule.Queue(session)) _ = CoalescedRenderAsync(session.TenantId, session.SessionId, schedule);
                break;
            case StreamNotification.Config config:
                BroadcastConfig(config.TenantId, config.Version);
                break;
            case StreamNotification.Player player:
                Deleted([new AccountDeletionPlayer(player.TenantId, player.PlayerId)]);
                break;
            case StreamNotification.Reconnected:
                _ = ReconcileAsync();
                break;
        }
    }

    private async Task CoalescedRenderAsync(Guid tenantId, Guid sessionId, SessionRenderSchedule schedule)
    {
        var key = (tenantId, sessionId);
        try
        {
            while (schedule.TakePending(out _, out var immediate, out var signal))
            {
                // A phase marker wakes the coalescer immediately. Other changes wait for the
                // next 250ms window, capping same-phase deliveries at four per second.
                if (!immediate) await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(250)), signal.Task);
                await RenderSessionAsync(tenantId, sessionId, CancellationToken.None, schedule);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to render stream session {SessionId}", sessionId);
        }
        finally
        {
            if (schedule.Stop())
            {
                _ = CoalescedRenderAsync(tenantId, sessionId, schedule);
            }
            else if (!_subscriptions.Values.Any(subscription => subscription.TenantId == tenantId && subscription.SessionId == sessionId))
            {
                _renderSchedules.TryRemove(new KeyValuePair<(Guid Tenant, Guid Session), SessionRenderSchedule>(key, schedule));
            }
        }
    }

    private sealed class SessionRenderSchedule
    {
        private readonly object _gate = new();
        private long _pendingVersion = -1;
        private string? _phase;
        private bool _immediate;
        private bool _running;
        private TaskCompletionSource _phaseChanged = NewSignal();

        public bool Queue(StreamNotification.Session notification)
        {
            lock (_gate)
            {
                _pendingVersion = Math.Max(_pendingVersion, notification.Version);
                var phaseChanged = notification.Phase is null || !StringComparer.Ordinal.Equals(_phase, notification.Phase);
                if (notification.Phase is not null) _phase = notification.Phase;
                if (phaseChanged)
                {
                    _immediate = true;
                    _phaseChanged.TrySetResult();
                }
                if (_running) return false;
                _running = true;
                return true;
            }
        }

        public bool TakePending(out long version, out bool immediate, out TaskCompletionSource signal)
        {
            lock (_gate)
            {
                version = _pendingVersion;
                immediate = _immediate;
                signal = _phaseChanged;
                if (version < 0) return false;
                _pendingVersion = -1;
                _immediate = false;
                _phaseChanged = NewSignal();
                return true;
            }
        }

        public bool Stop()
        {
            lock (_gate)
            {
                if (_pendingVersion >= 0) return true;
                _running = false;
                return false;
            }
        }

        public bool HasNewerVersion(long version)
        {
            lock (_gate) return _pendingVersion > version;
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task RenderSessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken,
        SessionRenderSchedule? schedule = null)
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
                // A later notification arrived while this snapshot was loading. Let its render
                // replace this stale result before a subscriber can observe it.
                if (schedule?.HasNewerVersion(state.Session.Version) == true) return;
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
