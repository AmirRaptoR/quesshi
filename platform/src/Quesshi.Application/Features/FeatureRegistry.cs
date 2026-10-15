using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Quesshi.Application.Jobs;
using Quesshi.Plugin.Api;

namespace Quesshi.Application;

/// <summary>Registers feature declarations with platform capabilities and durable-work registries.</summary>
internal sealed class FeatureRegistry(
    CapabilityRegistry capabilities,
    JobHandlerRegistry jobs,
    JobScheduleRegistry schedules)
{
    private readonly Dictionary<string, RegisteredFeature> _features = new(StringComparer.Ordinal);
    private readonly Dictionary<(string FeatureId, Type EventType), Delegate> _eventHandlers = [];
    private readonly Dictionary<(string FeatureId, Type EventType), Delegate> _declaredEventHandlers = [];
    private readonly Dictionary<string, Delegate> _cleanupHandlers = new(StringComparer.Ordinal);
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
        private readonly List<Action<RouteGroupBuilder>> _playerRoutes = [];
        private readonly List<Action<RouteGroupBuilder>> _openRoutes = [];
        private readonly Dictionary<AdminRole, List<Action<RouteGroupBuilder>>> _adminRoutes = [];

        public IReadOnlyCollection<FeatureEventHandler> EventHandlers =>
            _eventHandlers.Select(pair => new FeatureEventHandler(pair.Key, pair.Value)).ToArray();

        public IReadOnlyCollection<FeatureJob> Jobs => _jobs.Values;

        public IReadOnlyCollection<FeatureSchedule> Schedules => _schedules.Values;

        public IReadOnlyList<Action<RouteGroupBuilder>> PlayerRouteContributions => _playerRoutes;

        public IReadOnlyList<Action<RouteGroupBuilder>> OpenRouteContributions => _openRoutes;

        public IReadOnlyList<FeatureAdminRoutes> AdminRouteContributions => _adminRoutes
            .OrderBy(pair => pair.Key)
            .Select(pair => new FeatureAdminRoutes(pair.Key, pair.Value))
            .ToArray();

        public Delegate? CleanupHandler { get; private set; }

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
}
