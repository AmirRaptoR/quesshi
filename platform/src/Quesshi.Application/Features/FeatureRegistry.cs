using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Quesshi.Application.Jobs;
using Quesshi.Plugin.Api;

namespace Quesshi.Application;

/// <summary>Registers feature declarations with platform capabilities and durable-work registries.</summary>
internal sealed class FeatureRegistry(
    CapabilityRegistry capabilities,
    JobHandlerRegistry jobs,
    JobScheduleRegistry schedules) : IInboxItems, INamedQueries
{
    private readonly Dictionary<string, RegisteredFeature> _features = new(StringComparer.Ordinal);
    private readonly Dictionary<(string FeatureId, Type EventType), Delegate> _eventHandlers = [];
    private readonly Dictionary<(string FeatureId, Type EventType), Delegate> _declaredEventHandlers = [];
    private readonly Dictionary<string, Delegate> _cleanupHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InboxHandler> _inboxHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredNamedQuery> _namedQueries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _usedNamedQueries = new(StringComparer.Ordinal);
    private readonly List<FeatureRoutes> _routes = [];

    public IReadOnlyCollection<RegisteredFeature> Features => _features.Values;

    public CapabilityRegistry Capabilities => capabilities;

    public JobHandlerRegistry Jobs => jobs;

    public JobScheduleRegistry Schedules => schedules;

    public IReadOnlyCollection<FeatureRoutes> Routes => _routes;

    /// <summary>Contains only handlers that should receive events emitted after startup.</summary>
    public IReadOnlyDictionary<(string FeatureId, Type EventType), Delegate> EventHandlers => _eventHandlers;

    /// <summary>Contains all declared handlers, including retired features, for persisted-state readers.</summary>
    public IReadOnlyDictionary<(string FeatureId, Type EventType), Delegate> DeclaredEventHandlers => _declaredEventHandlers;

    /// <summary>Contains cleanup declarations, including those from retired features.</summary>
    public IReadOnlyDictionary<string, Delegate> CleanupHandlers => _cleanupHandlers;

    public void Register(IFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.Id);
        ArgumentNullException.ThrowIfNull(feature.Settings);

        var id = feature.Id.Trim();
        if (_features.ContainsKey(id))
        {
            throw new InvalidOperationException($"Feature '{id}' is already registered.");
        }

        var settingsErrors = feature.Settings.Validate(feature.Settings.DefaultSettings);
        if (settingsErrors.Count > 0)
        {
            throw new InvalidOperationException($"Feature '{id}' has invalid default settings: {string.Join(" ", settingsErrors)}");
        }

        var builder = new FeatureBuilder(id);
        feature.Configure(builder);

        capabilities.Register(CapabilityType.Feature, id, feature.Settings);

        foreach (var job in builder.Jobs)
        {
            jobs.Register(id, job.Kind, (context, cancellationToken) => job.Handler(context, cancellationToken), job.WhenDisabled);
        }

        if (!feature.Retired)
        {
            foreach (var schedule in builder.Schedules)
            {
                schedules.Every(id, schedule.Interval, schedule.Kind);
            }

            foreach (var eventHandler in builder.EventHandlers)
            {
                _declaredEventHandlers.Add((id, eventHandler.EventType), eventHandler.Handler);
                _eventHandlers.Add((id, eventHandler.EventType), eventHandler.Handler);
            }
        }
        else
        {
            foreach (var eventHandler in builder.EventHandlers)
            {
                _declaredEventHandlers.Add((id, eventHandler.EventType), eventHandler.Handler);
            }
        }

        if (builder.CleanupHandler is not null)
        {
            _cleanupHandlers.Add(id, builder.CleanupHandler);
        }

        if (builder.InboxHandler is not null)
        {
            _inboxHandlers.Add(id, builder.InboxHandler);
        }

        foreach (var query in builder.NamedQueries)
        {
            if (!_namedQueries.TryAdd(query.Name, new RegisteredNamedQuery(id, query.Handler)))
            {
                throw new InvalidOperationException($"Named query '{query.Name}' already has a provider.");
            }
        }

        _usedNamedQueries.Add(id, [.. builder.UsedNamedQueries]);

        if (!feature.Retired && (builder.PlayerRouteContributions.Count != 0 || builder.OpenRouteContributions.Count != 0 || builder.AdminRouteContributions.Count != 0))
        {
            _routes.Add(new FeatureRoutes(id, builder.PlayerRouteContributions, builder.OpenRouteContributions, builder.AdminRouteContributions));
        }

        _features.Add(id, new RegisteredFeature(id, feature.Retired, feature.Settings));
    }

    public static IReadOnlyList<IFeature> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IFeature).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => Activator.CreateInstance(type) as IFeature
                ?? throw new InvalidOperationException($"Feature '{type.FullName}' requires a public parameterless constructor."))
            .ToArray();
    }

    public static FeatureRegistry Create(IEnumerable<IFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var capabilities = CoreCapabilities.CreateRegistry();
        var jobs = new JobHandlerRegistry();
        var schedules = new JobScheduleRegistry(jobs);
        var registry = new FeatureRegistry(capabilities, jobs, schedules);
        foreach (var feature in features) registry.Register(feature);
        return registry;
    }

    internal sealed record RegisteredFeature(string Id, bool Retired, FeatureSettings Settings);

    public Task InvokeEventHandlerAsync(string featureId, Type eventType, ITenantTx transaction, object @event, CancellationToken cancellationToken)
    {
        if (!_declaredEventHandlers.TryGetValue((featureId, eventType), out var handler))
        {
            throw new InvalidOperationException($"Feature '{featureId}' has no declared handler for event '{eventType.FullName}'.");
        }

        return handler.DynamicInvoke(transaction, @event, cancellationToken) as Task
            ?? throw new InvalidOperationException($"Feature '{featureId}' event handler returned no task.");
    }

    public Task InvokeCleanupAsync(string featureId, ITenantTx transaction, Guid playerId, CancellationToken cancellationToken)
    {
        if (!_cleanupHandlers.TryGetValue(featureId, out var handler))
        {
            throw new InvalidOperationException($"Feature '{featureId}' has no player cleanup handler.");
        }

        return handler.DynamicInvoke(transaction, playerId, cancellationToken) as Task
            ?? throw new InvalidOperationException($"Feature '{featureId}' cleanup handler returned no task.");
    }

    public async Task<IReadOnlyList<InboxItem>> GetInboxItemsAsync(
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var items = new List<InboxItem>();
        foreach (var (featureId, handler) in _inboxHandlers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var enabled = await transaction.QuerySingleOrDefaultAsync<bool?>(
                "select entitled and enabled from tenant_capabilities where type = 'feature' and key = @featureId",
                new { featureId },
                cancellationToken);
            if (enabled != true) continue;

            var contributed = await handler(transaction, playerId, cancellationToken);
            ArgumentNullException.ThrowIfNull(contributed);
            items.AddRange(contributed);
        }

        return items;
    }

    public async Task<NamedQueryResult<TResult>> InvokeAsync<TResult>(
        string consumerFeatureId,
        string name,
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerFeatureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(transaction);

        var normalizedConsumerFeatureId = consumerFeatureId.Trim();
        var normalizedName = name.Trim();
        if (!_features.ContainsKey(normalizedConsumerFeatureId))
        {
            throw new InvalidOperationException($"Feature '{normalizedConsumerFeatureId}' is not registered.");
        }

        if (!_usedNamedQueries[normalizedConsumerFeatureId].Contains(normalizedName))
        {
            throw new InvalidOperationException($"Feature '{normalizedConsumerFeatureId}' has not declared use of named query '{normalizedName}'.");
        }

        if (!_namedQueries.TryGetValue(normalizedName, out var query))
        {
            return NamedQueryResult<TResult>.Unavailable;
        }

        if (query.Handler.ResultType != typeof(TResult))
        {
            throw new InvalidOperationException($"Named query '{normalizedName}' returns '{query.Handler.ResultType.FullName}', not '{typeof(TResult).FullName}'.");
        }

        var enabled = await transaction.QuerySingleOrDefaultAsync<bool?>(
            "select entitled and enabled from tenant_capabilities where type = 'feature' and key = @featureId",
            new { featureId = query.ProviderFeatureId },
            cancellationToken);
        if (enabled != true)
        {
            return NamedQueryResult<TResult>.Unavailable;
        }

        var value = await query.Handler.InvokeAsync(transaction, playerId, cancellationToken);
        return new NamedQueryResult<TResult>(true, value is null ? default : (TResult)value);
    }

    internal sealed record FeatureRoutes(
        string Id,
        IReadOnlyList<Action<RouteGroupBuilder>> Player,
        IReadOnlyList<Action<RouteGroupBuilder>> Open,
        IReadOnlyList<FeatureAdminRoutes> Admin);

    internal sealed record FeatureAdminRoutes(AdminRole MinimumRole, IReadOnlyList<Action<RouteGroupBuilder>> Contributions);

    private sealed class FeatureBuilder(string featureId) : IFeatureBuilder
    {
        private readonly Dictionary<Type, Delegate> _eventHandlers = [];
        private readonly Dictionary<string, FeatureJob> _jobs = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Kind, TimeSpan Interval), FeatureSchedule> _schedules = [];
        private readonly Dictionary<string, NamedQueryHandlerAdapter> _namedQueries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedNamedQueries = new(StringComparer.Ordinal);
        private readonly List<Action<RouteGroupBuilder>> _playerRoutes = [];
        private readonly List<Action<RouteGroupBuilder>> _openRoutes = [];
        private readonly Dictionary<AdminRole, List<Action<RouteGroupBuilder>>> _adminRoutes = [];

        public IReadOnlyCollection<FeatureEventHandler> EventHandlers =>
            _eventHandlers.Select(pair => new FeatureEventHandler(pair.Key, pair.Value)).ToArray();

        public IReadOnlyCollection<FeatureJob> Jobs => _jobs.Values;

        public IReadOnlyCollection<FeatureSchedule> Schedules => _schedules.Values;

        public IReadOnlyCollection<FeatureNamedQuery> NamedQueries => _namedQueries
            .Select(pair => new FeatureNamedQuery(pair.Key, pair.Value))
            .ToArray();

        public IReadOnlySet<string> UsedNamedQueries => _usedNamedQueries;

        public IReadOnlyList<Action<RouteGroupBuilder>> PlayerRouteContributions => _playerRoutes;

        public IReadOnlyList<Action<RouteGroupBuilder>> OpenRouteContributions => _openRoutes;

        public IReadOnlyList<FeatureAdminRoutes> AdminRouteContributions => _adminRoutes
            .OrderBy(pair => pair.Key)
            .Select(pair => new FeatureAdminRoutes(pair.Key, pair.Value))
            .ToArray();

        public Delegate? CleanupHandler { get; private set; }

        public InboxHandler? InboxHandler { get; private set; }

        public void PlayerRoutes(Action<RouteGroupBuilder> routes)
        {
            ArgumentNullException.ThrowIfNull(routes);
            _playerRoutes.Add(routes);
        }

        public void OpenRoutes(Action<RouteGroupBuilder> routes)
        {
            ArgumentNullException.ThrowIfNull(routes);
            _openRoutes.Add(routes);
        }

        public void AdminRoutes(AdminRole minimumRole, Action<RouteGroupBuilder> routes)
        {
            if (!Enum.IsDefined(minimumRole))
                throw new ArgumentOutOfRangeException(nameof(minimumRole));
            ArgumentNullException.ThrowIfNull(routes);
            if (!_adminRoutes.TryGetValue(minimumRole, out var contributions))
            {
                contributions = [];
                _adminRoutes.Add(minimumRole, contributions);
            }
            contributions.Add(routes);
        }

        public void On<TEvent>(Func<ITenantTx, TEvent, CancellationToken, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!_eventHandlers.TryAdd(typeof(TEvent), handler))
            {
                throw new InvalidOperationException($"Feature '{featureId}' already has a handler for event '{typeof(TEvent).FullName}'.");
            }
        }

        public void CleanupPlayer(Func<ITenantTx, Guid, CancellationToken, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (CleanupHandler is not null)
            {
                throw new InvalidOperationException($"Feature '{featureId}' already has a player cleanup handler.");
            }

            CleanupHandler = handler;
        }

        public void Inbox(InboxHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (InboxHandler is not null)
            {
                throw new InvalidOperationException($"Feature '{featureId}' already has an inbox handler.");
            }

            InboxHandler = handler;
        }

        public void Provides<TResult>(string name, NamedQueryHandler<TResult> handler)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(handler);

            var normalizedName = name.Trim();
            if (!_namedQueries.TryAdd(normalizedName, new NamedQueryHandlerAdapter<TResult>(handler)))
            {
                throw new InvalidOperationException($"Feature '{featureId}' already provides named query '{normalizedName}'.");
            }
        }

        public void Uses(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            var normalizedName = name.Trim();
            if (!_usedNamedQueries.Add(normalizedName))
            {
                throw new InvalidOperationException($"Feature '{featureId}' already uses named query '{normalizedName}'.");
            }
        }

        public void Job(string kind, Func<JobContext, CancellationToken, Task> handler, WhenDisabled whenDisabled = WhenDisabled.Complete)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(handler);
            if (!Enum.IsDefined(whenDisabled)) throw new ArgumentOutOfRangeException(nameof(whenDisabled));

            var normalizedKind = kind.Trim();
            if (!_jobs.TryAdd(normalizedKind, new FeatureJob(normalizedKind, handler, whenDisabled)))
            {
                throw new InvalidOperationException($"Feature '{featureId}' already has a handler for job '{normalizedKind}'.");
            }
        }

        public void Every(TimeSpan interval, string kind)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

            var normalizedKind = kind.Trim();
            if (!_schedules.TryAdd((normalizedKind, interval), new FeatureSchedule(interval, normalizedKind)))
            {
                throw new InvalidOperationException($"Feature '{featureId}' already schedules job '{normalizedKind}' every {interval}.");
            }
        }
    }

    private sealed record FeatureEventHandler(Type EventType, Delegate Handler);

    private sealed record FeatureJob(string Kind, Func<JobContext, CancellationToken, Task> Handler, WhenDisabled WhenDisabled);

    private sealed record FeatureSchedule(TimeSpan Interval, string Kind);

    private sealed record FeatureNamedQuery(string Name, NamedQueryHandlerAdapter Handler);

    private sealed record RegisteredNamedQuery(string ProviderFeatureId, NamedQueryHandlerAdapter Handler);

    private abstract class NamedQueryHandlerAdapter
    {
        public abstract Type ResultType { get; }

        public abstract Task<object?> InvokeAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken);
    }

    private sealed class NamedQueryHandlerAdapter<TResult>(NamedQueryHandler<TResult> handler) : NamedQueryHandlerAdapter
    {
        public override Type ResultType => typeof(TResult);

        public override async Task<object?> InvokeAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
            await handler(transaction, playerId, cancellationToken);
    }
}
