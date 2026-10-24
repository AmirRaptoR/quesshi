using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Quesshi.Application.Inbox;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server.Accounts;
using Quesshi.Server.Security;

namespace Quesshi.Server.Streaming;

internal enum StreamMessageKind { View, Config, Inbox, Effects, Gone, Closed }

internal enum StreamCloseReason { SignedOut, Banned, Deleted, Expired }

internal sealed record StreamMessage(StreamMessageKind Kind, long Version, string? Json = null, string? Reason = null,
    bool AllowsSameVersion = false);

/// <summary>One bounded local stream subscription. Delivery is always convergence-oriented.</summary>
internal sealed class StreamSubscription(Guid tenantId, string playerId, Guid? sessionId, bool isGuest = false,
    string? authenticationSessionKey = null)
{
    private readonly object _gate = new();
    private readonly Channel<StreamMessage> _messages = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private bool _terminal;
    private StreamMessage? _queued;
    private StreamMessage? _pendingView;
    private StreamMessage? _pendingConfig;
    private StreamMessage? _pendingInbox;
    private StreamMessage? _pendingEffects;

    public Guid TenantId { get; } = tenantId;
    public string PlayerId { get; } = playerId;
    public Guid? SessionId { get; } = sessionId;
    public bool IsGuest { get; } = isGuest;
    public string? AuthenticationSessionKey { get; } = authenticationSessionKey;
    public ChannelReader<StreamMessage> Messages => _messages.Reader;
    public long LastSentVersion { get; private set; } = -1;
    public long LastQueuedVersion { get; private set; } = -1;
    public long LastConfigVersion { get; private set; }
    public long LastInboxVersion { get; private set; } = -1;
    public long LastEffectsRevision { get; private set; } = -1;

    public void QueueView(RenderedSessionView view, bool allowSameVersion = false)
    {
        lock (_gate)
        {
            var newestVersion = Math.Max(LastSentVersion, LastQueuedVersion);
            if (_terminal || view.Version < newestVersion || (!allowSameVersion && view.Version == newestVersion)) return;
            LastQueuedVersion = view.Version;
            var message = new StreamMessage(StreamMessageKind.View, view.Version, view.Json, AllowsSameVersion: allowSameVersion);
            if (_queued?.Kind == StreamMessageKind.View)
            {
                _queued = message;
                _messages.Writer.TryWrite(message);
                return;
            }
            _pendingView = message;
            QueuePending();
        }
    }

    public void QueueConfig(long version)
    {
        lock (_gate)
        {
            if (_terminal || version <= Math.Max(LastConfigVersion, PendingVersion(StreamMessageKind.Config))) return;
            _pendingConfig = new StreamMessage(StreamMessageKind.Config, version);
            QueuePending();
        }
    }

    public void QueueInbox(long version, string? json = null)
    {
        lock (_gate)
        {
            if (_terminal || version <= Math.Max(LastInboxVersion, PendingVersion(StreamMessageKind.Inbox))) return;
            _pendingInbox = new StreamMessage(StreamMessageKind.Inbox, version, json);
            QueuePending();
        }
    }

    public void QueueEffects(long revision, string json)
    {
        lock (_gate)
        {
            if (_terminal || revision <= Math.Max(LastEffectsRevision, PendingVersion(StreamMessageKind.Effects))) return;
            _pendingEffects = new StreamMessage(StreamMessageKind.Effects, revision, json);
            QueuePending();
        }
    }

    public void QueueGone(string reason)
    {
        QueueTerminal(StreamMessageKind.Gone, reason);
    }

    public void QueueClosed(StreamCloseReason reason)
    {
        QueueTerminal(StreamMessageKind.Closed, reason switch
        {
            StreamCloseReason.SignedOut => "signed_out",
            StreamCloseReason.Banned => "banned",
            StreamCloseReason.Deleted => "deleted",
            StreamCloseReason.Expired => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        });
    }

    private void QueueTerminal(StreamMessageKind kind, string reason)
    {
        lock (_gate)
        {
            if (_terminal) return;
            _terminal = true;
            _queued = null;
            _pendingView = null;
            _pendingConfig = null;
            _pendingInbox = null;
            _pendingEffects = null;
            _messages.Writer.TryWrite(new StreamMessage(kind, 0, Reason: reason));
            _messages.Writer.TryComplete();
        }
    }

    public bool MarkSent(StreamMessage message)
    {
        lock (_gate)
        {
            if (message.Kind == StreamMessageKind.View)
            {
                if (message.Version < LastSentVersion || (message.Version == LastSentVersion && !message.AllowsSameVersion)) return false;
                LastSentVersion = Math.Max(LastSentVersion, message.Version);
                Sent(message);
                return true;
            }
            if (message.Kind == StreamMessageKind.Config)
            {
                if (message.Version <= LastConfigVersion) return false;
                LastConfigVersion = message.Version;
                Sent(message);
            }
            if (message.Kind == StreamMessageKind.Inbox)
            {
                if (message.Version <= LastInboxVersion) return false;
                LastInboxVersion = message.Version;
                Sent(message);
            }
            if (message.Kind == StreamMessageKind.Effects)
            {
                if (message.Version <= LastEffectsRevision) return false;
                LastEffectsRevision = message.Version;
                Sent(message);
            }
            return true;
        }
    }

    public bool NeedsView(long version)
    {
        lock (_gate) return !_terminal && version > Math.Max(LastSentVersion, LastQueuedVersion);
    }

    public bool IsTerminal
    {
        get { lock (_gate) return _terminal; }
    }

    public bool NeedsInbox(long version)
    {
        lock (_gate) return !_terminal && version > Math.Max(LastInboxVersion, PendingVersion(StreamMessageKind.Inbox));
    }

    public bool NeedsEffects(long revision)
    {
        lock (_gate) return !_terminal && revision > Math.Max(LastEffectsRevision, PendingVersion(StreamMessageKind.Effects));
    }

    public void Complete()
    {
        lock (_gate)
        {
            _terminal = true;
            _queued = null;
            _pendingView = null;
            _pendingConfig = null;
            _pendingInbox = null;
            _pendingEffects = null;
            _messages.Writer.TryComplete();
        }
    }

    private void Sent(StreamMessage message)
    {
        if (_queued == message) _queued = null;
        QueuePending();
    }

    private long PendingVersion(StreamMessageKind kind)
    {
        var queued = _queued?.Kind == kind ? _queued.Version : -1;
        var pending = kind switch
        {
            StreamMessageKind.Config => _pendingConfig?.Version ?? -1,
            StreamMessageKind.Inbox => _pendingInbox?.Version ?? -1,
            StreamMessageKind.Effects => _pendingEffects?.Version ?? -1,
            _ => _pendingView?.Version ?? -1
        };
        return Math.Max(queued, pending);
    }

    private void QueuePending()
    {
        if (_terminal || _queued is not null) return;
        var next = _pendingView ?? _pendingEffects ?? _pendingInbox ?? _pendingConfig;
        if (next is null) return;
        if (next.Kind == StreamMessageKind.View) _pendingView = null;
        else if (next.Kind == StreamMessageKind.Inbox) _pendingInbox = null;
        else if (next.Kind == StreamMessageKind.Effects) _pendingEffects = null;
        else _pendingConfig = null;
        _queued = next;
        _messages.Writer.TryWrite(next);
    }
}

/// <summary>
/// Per-silo stream coordinator. It performs one snapshot/shared render for each watched session
/// and only keeps the newest personal view per client.
/// </summary>
internal sealed class StreamHub : BackgroundService, IAccountDeletionNotifier, IPlayerAccessRevocationNotifier
{
    private static readonly TimeSpan ScoreCoalesceWindow = TimeSpan.FromMilliseconds(250);
    private readonly ISessionSnapshotLoader snapshots;
    private readonly IViewRenderer renderer;
    private readonly IFormatRegistry formats;
    private readonly ITenantDb database;
    private readonly IStreamNotificationSource notifications;
    private readonly IClock clock;
    private readonly ILogger<StreamHub> logger;
    private readonly IStreamSessionLiveness sessionLiveness;
    private readonly InboxReader? inboxReader;
    private readonly ISessionEffects? effects;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan scoreCoalesceWindow;
    private readonly ConcurrentDictionary<Guid, StreamSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), SessionGate> _sessionGates = new();
    private readonly object _sessionGateLock = new();
    private readonly SemaphoreSlim _renderJobs = new(8, 8);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), SessionRenderSchedule> _renderSchedules = new();
    private readonly ConcurrentDictionary<Guid, PresenceRefresh> _presenceRefreshes = new();
    private int _disposed;

    public StreamHub(ISessionSnapshotLoader snapshots, IViewRenderer renderer, IFormatRegistry formats, ITenantDb database,
        IStreamNotificationSource notifications, IClock clock, ILogger<StreamHub> logger, IStreamSessionLiveness sessionLiveness)
        : this(snapshots, renderer, formats, database, notifications, clock, logger, ScoreCoalesceWindow, sessionLiveness, null)
    {
    }

    public StreamHub(ISessionSnapshotLoader snapshots, IViewRenderer renderer, IFormatRegistry formats, ITenantDb database,
        IStreamNotificationSource notifications, IClock clock, ILogger<StreamHub> logger, IStreamSessionLiveness sessionLiveness,
        InboxReader inboxReader)
        : this(snapshots, renderer, formats, database, notifications, clock, logger, ScoreCoalesceWindow, sessionLiveness, inboxReader)
    {
    }

    public StreamHub(ISessionSnapshotLoader snapshots, IViewRenderer renderer, IFormatRegistry formats, ITenantDb database,
        IStreamNotificationSource notifications, IClock clock, ILogger<StreamHub> logger, IStreamSessionLiveness sessionLiveness,
        InboxReader inboxReader, ISessionEffects effects)
        : this(snapshots, renderer, formats, database, notifications, clock, logger, ScoreCoalesceWindow, sessionLiveness, inboxReader, effects)
    {
    }

    internal StreamHub(ISessionSnapshotLoader snapshots, IViewRenderer renderer, IFormatRegistry formats, ITenantDb database,
        IStreamNotificationSource notifications, IClock clock, ILogger<StreamHub> logger, TimeSpan scoreCoalesceWindow,
        IStreamSessionLiveness sessionLiveness, InboxReader? inboxReader = null, ISessionEffects? effects = null)
    {
        this.snapshots = snapshots;
        this.renderer = renderer;
        this.formats = formats;
        this.database = database;
        this.notifications = notifications;
        this.clock = clock;
        this.logger = logger;
        this.scoreCoalesceWindow = scoreCoalesceWindow;
        this.sessionLiveness = sessionLiveness;
        this.inboxReader = inboxReader;
        this.effects = effects;
        notifications.Received += OnNotification;
    }

    public StreamSubscription Subscribe(Guid tenantId, string playerId, Guid? sessionId, string? authenticationSessionKey = null,
        bool isGuest = false)
    {
        var subscription = new StreamSubscription(tenantId, playerId, sessionId, isGuest, authenticationSessionKey);
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
            ClosePlayer(player.TenantId, player.PlayerId, StreamCloseReason.Deleted);
        }
    }

    public async Task RenderInitialAsync(StreamSubscription subscription, CancellationToken cancellationToken)
    {
        if (subscription.SessionId is { } sessionId) await RenderSessionAsync(subscription.TenantId, sessionId, cancellationToken);
        if (!Guid.TryParse(subscription.PlayerId, out var playerId)) return;
        if (inboxReader is not null)
        {
            await QueueInboxSnapshotAsync(subscription, playerId, cancellationToken);
            return;
        }
        var version = await database.InTenantAsync(subscription.TenantId, transaction => transaction.QuerySingleOrDefaultAsync<long?>("""
            select version
            from player_inbox
            where player_id = @playerId
            """, new { playerId }, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        if (version is { } current) subscription.QueueInbox(current);
    }

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
            var sessionKeys = tenant.Where(subscription => subscription.AuthenticationSessionKey is not null)
                .Select(subscription => subscription.AuthenticationSessionKey!).Distinct(StringComparer.Ordinal).ToArray();
            var state = await database.InTenantAsync(tenant.Key, async transaction =>
            {
                var sessionVersions = watched.Length == 0
                    ? []
                    : await transaction.QueryAsync<SessionVersion>("""
                        select id as SessionId, version as Version, status as Status
                        from sessions
                        where id = any(@sessionIds)
                        """, new { sessionIds = watched }, cancellationToken);
                var playerStates = players.Length == 0
                    ? []
                    : await transaction.QueryAsync<PlayerAccessState>("""
                        select id as PlayerId, deleted_at is not null as IsDeleted, banned_at is not null as IsBanned
                        from players
                        where id = any(@playerIds)
                        """, new { playerIds = players }, cancellationToken);
                var inboxVersions = players.Length == 0
                    ? []
                    : await transaction.QueryAsync<InboxVersion>("""
                        select player_id as PlayerId, version as Version
                        from player_inbox
                        where player_id = any(@playerIds)
                        """, new { playerIds = players }, cancellationToken);
                var effectsVersions = watched.Length == 0
                    ? []
                    : await transaction.QueryAsync<EffectsVersion>("""
                        select effects.session_id as SessionId, effects.revision as Revision
                        from session_effects effects
                        join sessions on sessions.id = effects.session_id
                        where effects.session_id = any(@sessionIds)
                          and sessions.status in ('finished', 'cancelled')
                        """, new { sessionIds = watched }, cancellationToken);
                var configVersion = await transaction.QuerySingleOrDefaultAsync<long?>(
                    "select config_version from tenants where id = @tenantId", new { tenantId = tenant.Key }, cancellationToken);
                return new ReconciliationState(sessionVersions, playerStates, inboxVersions, effectsVersions, configVersion);
            }, cancellationToken: cancellationToken, readOnly: true);

            var versions = state.SessionVersions.ToDictionary(version => version.SessionId);
            var playerStates = state.PlayerStates.ToDictionary(player => player.PlayerId);
            var sessionStatuses = await sessionLiveness.StatusAsync(tenant.Key, sessionKeys, cancellationToken);
            foreach (var subscription in tenant)
            {
                if (Guid.TryParse(subscription.PlayerId, out var playerId))
                {
                    if (!playerStates.TryGetValue(playerId, out var player) || player.IsDeleted)
                    {
                        subscription.QueueClosed(StreamCloseReason.Deleted);
                        continue;
                    }
                    if (player.IsBanned)
                    {
                        subscription.QueueClosed(StreamCloseReason.Banned);
                        continue;
                    }
                }
                if (subscription.AuthenticationSessionKey is { } key
                    && (!sessionStatuses.TryGetValue(key, out var status) || status != StreamSessionStatus.Live))
                {
                    subscription.QueueClosed(status == StreamSessionStatus.Expired
                        ? StreamCloseReason.Expired
                        : StreamCloseReason.SignedOut);
                }
            }
            var active = tenant.Where(subscription => !subscription.IsTerminal).ToArray();
            var activePlayers = active.Where(subscription => !subscription.IsGuest)
                .Select(subscription => Guid.TryParse(subscription.PlayerId, out var playerId) ? playerId : (Guid?)null)
                .Where(playerId => playerId is not null && playerStates.TryGetValue(playerId.Value, out var player) && !player.IsDeleted && !player.IsBanned)
                .Select(playerId => playerId!.Value).Distinct().ToArray();
            var presenceRefreshed = await RefreshPresenceAsync(tenant.Key, activePlayers, cancellationToken);
            var activeWatched = active.Where(subscription => subscription.SessionId is not null)
                .Select(subscription => subscription.SessionId!.Value).Distinct().ToArray();
            foreach (var sessionId in activeWatched)
            {
                var targets = active.Where(subscription => subscription.SessionId == sessionId).ToArray();
                if (!versions.TryGetValue(sessionId, out var version) || targets.Any(target => target.NeedsView(version.Version)))
                {
                    await RenderSessionAsync(tenant.Key, sessionId, cancellationToken);
                }
            }
            if (presenceRefreshed)
            {
                foreach (var lobby in state.SessionVersions.Where(session => session.Status == "lobby" && activeWatched.Contains(session.SessionId)))
                    await RenderSessionAsync(tenant.Key, lobby.SessionId, cancellationToken, force: true);
            }
            foreach (var inbox in state.InboxVersions) await BroadcastInboxAsync(tenant.Key, inbox.PlayerId, inbox.Version, cancellationToken);
            foreach (var effect in state.EffectsVersions)
            {
                if (tenant.Any(subscription => subscription.SessionId == effect.SessionId && subscription.NeedsEffects(effect.Revision)))
                    await DeliverEffectsAsync(tenant.Key, effect.SessionId, effect.Revision, cancellationToken);
            }
            if (state.ConfigVersion is { } current) BroadcastConfig(tenant.Key, current);
        }
    }

    internal sealed record SessionVersion(Guid SessionId, long Version, string Status = "running");
    internal sealed record PlayerAccessState(Guid PlayerId, bool IsDeleted, bool IsBanned);
    internal sealed record InboxVersion(Guid PlayerId, long Version);
    internal sealed record EffectsVersion(Guid SessionId, long Revision);
    private sealed record ReconciliationState(IReadOnlyList<SessionVersion> SessionVersions, IReadOnlyList<PlayerAccessState> PlayerStates,
        IReadOnlyList<InboxVersion> InboxVersions, IReadOnlyList<EffectsVersion> EffectsVersions, long? ConfigVersion);

    private async Task<bool> RefreshPresenceAsync(Guid tenantId, IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken)
    {
        var refresh = _presenceRefreshes.GetOrAdd(tenantId, _ => new PresenceRefresh());
        await refresh.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.Now;
            if (refresh.LastRefreshAt is { } last && now - last < PresenceTiming.RefreshInterval) return false;
            if (playerIds.Count > 0)
            {
                await database.InTenantAsync(tenantId, transaction => transaction.ExecuteAsync("""
                    update players
                    set last_seen_at = @now
                    where id = any(@playerIds)
                      and deleted_at is null
                      and banned_at is null
                    """, new { now, playerIds }, cancellationToken), cancellationToken: cancellationToken);
            }
            refresh.LastRefreshAt = now;
            return true;
        }
        finally { refresh.Gate.Release(); }
    }

    private sealed class PresenceRefresh
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset? LastRefreshAt { get; set; }
    }

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
            case StreamNotification.Inbox inbox:
                _ = DeliverInboxAsync(inbox.TenantId, inbox.PlayerId, inbox.Version);
                break;
            case StreamNotification.Effects effect:
                _ = DeliverEffectsAsync(effect.TenantId, effect.SessionId, effect.Revision, CancellationToken.None);
                break;
            case StreamNotification.Player player:
                ClosePlayer(player.TenantId, player.PlayerId, player.Reason == "banned"
                    ? StreamCloseReason.Banned
                    : StreamCloseReason.Deleted);
                break;
            case StreamNotification.AuthenticationSession session:
                foreach (var subscription in _subscriptions.Values.Where(subscription =>
                             subscription.TenantId == session.TenantId
                             && StringComparer.Ordinal.Equals(subscription.AuthenticationSessionKey, session.SessionKey)))
                {
                    subscription.QueueClosed(StreamCloseReason.SignedOut);
                }
                break;
            case StreamNotification.Reconnected:
                _ = ReconcileAsync();
                break;
        }
    }

    public void RevokePlayer(Guid tenantId, Guid playerId) => ClosePlayer(tenantId, playerId, StreamCloseReason.Banned);

    private void ClosePlayer(Guid tenantId, Guid playerId, StreamCloseReason reason)
    {
        foreach (var subscription in _subscriptions.Values.Where(subscription =>
                     subscription.TenantId == tenantId
                     && Guid.TryParse(subscription.PlayerId, out var subscribedPlayerId)
                     && subscribedPlayerId == playerId))
        {
            subscription.QueueClosed(reason);
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
                if (!immediate) await Task.WhenAny(Task.Delay(scoreCoalesceWindow), signal.Task);
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
                // A score-only delivery waits on this signal. Keep it active until a
                // phase notification can complete it; immediate deliveries do not wait.
                if (immediate) _phaseChanged = NewSignal();
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
        SessionRenderSchedule? schedule = null, bool force = false)
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
                if (!force && !targets.Any(target => target.NeedsView(state.Session.Version))) return;
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
                    else target.QueueView(view, force);
                }
                if (state.Session.Ended && targets.Any(target => target.NeedsEffects(0)))
                    await DeliverEffectsAsync(tenantId, sessionId, 0, cancellationToken);
            }
            finally { _renderJobs.Release(); }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseSessionGate(key, gate);
        }
    }

    private async Task DeliverEffectsAsync(Guid tenantId, Guid sessionId, long revision, CancellationToken cancellationToken)
    {
        if (effects is null) return;
        var targets = _subscriptions.Values.Where(subscription => subscription.TenantId == tenantId
            && subscription.SessionId == sessionId && subscription.NeedsEffects(revision)).ToArray();
        if (targets.Length == 0) return;

        var state = await snapshots.LoadAsync(tenantId, sessionId, cancellationToken);
        if (state is null || !state.Session.Ended) return;
        targets = targets.Where(target => state.Seats.ContainsKey(target.PlayerId) && target.NeedsEffects(revision)).ToArray();
        if (targets.Length == 0) return;

        var current = await database.InTenantAsync(tenantId,
            transaction => effects.GetAsync(transaction, sessionId, cancellationToken), cancellationToken: cancellationToken, readOnly: true);
        foreach (var target in targets) target.QueueEffects(current.Revision, JsonSerializer.Serialize(current, JsonOptions));
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

    private async Task BroadcastInboxAsync(Guid tenantId, Guid playerId, long version, CancellationToken cancellationToken)
    {
        var targets = _subscriptions.Values.Where(subscription =>
                     subscription.TenantId == tenantId
                     && Guid.TryParse(subscription.PlayerId, out var subscribedPlayerId)
                     && subscribedPlayerId == playerId
                     && subscription.NeedsInbox(version)).ToArray();
        foreach (var subscription in targets)
        {
            if (inboxReader is null)
            {
                subscription.QueueInbox(version);
                continue;
            }
            await QueueInboxSnapshotAsync(subscription, playerId, cancellationToken);
        }
    }

    private async Task DeliverInboxAsync(Guid tenantId, Guid playerId, long version)
    {
        try
        {
            await BroadcastInboxAsync(tenantId, playerId, version, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to deliver inbox stream update for player {PlayerId}", playerId);
        }
    }

    private async Task QueueInboxSnapshotAsync(StreamSubscription subscription, Guid playerId, CancellationToken cancellationToken)
    {
        if (inboxReader is null) return;
        var snapshot = await inboxReader.ReadAsync(subscription.TenantId, playerId, subscription.IsGuest, cancellationToken);
        subscription.QueueInbox(snapshot.Version, JsonSerializer.Serialize(snapshot, InboxJson));
    }

    private static readonly JsonSerializerOptions InboxJson = new(JsonSerializerDefaults.Web);

    public override void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        notifications.Received -= OnNotification;
        foreach (var subscription in _subscriptions.Values) subscription.Complete();
        base.Dispose();
    }
}
