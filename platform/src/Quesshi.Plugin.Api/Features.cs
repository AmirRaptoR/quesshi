using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Quesshi.Plugin.Api;

/// <summary>Describes the tenant settings supplied by a feature.</summary>
public abstract record FeatureSettings
{
    public abstract Type SettingsType { get; }

    public abstract object DefaultSettings { get; }

    public abstract IReadOnlyList<string> Validate(object settings);

    public static FeatureSettings<TSettings> Create<TSettings>(
        TSettings defaultSettings,
        Func<TSettings, IReadOnlyList<string>> validator)
        where TSettings : notnull => new(defaultSettings, validator);
}

/// <summary>Strongly typed feature settings with their defaults and validation.</summary>
public sealed record FeatureSettings<TSettings>(
    TSettings Defaults,
    Func<TSettings, IReadOnlyList<string>> Validator) : FeatureSettings
    where TSettings : notnull
{
    public override Type SettingsType => typeof(TSettings);

    public override object DefaultSettings => Defaults;

    public IReadOnlyList<string> Validate(TSettings settings) => Validator(settings);

    public override IReadOnlyList<string> Validate(object settings) =>
        settings is TSettings typed ? Validate(typed) : ["Settings have the wrong type."];
}

/// <summary>Declares the server-side contributions of one feature plugin.</summary>
public interface IFeature
{
    string Id { get; }

    FeatureSettings Settings { get; }

    /// <summary>
    /// A retired feature retains queued-work and cleanup metadata, but accepts no new events or schedules.
    /// </summary>
    bool Retired => false;

    void Configure(IFeatureBuilder feature);
}

/// <summary>Declares a feature's future event, cleanup, job, and schedule contributions.</summary>
public interface IFeatureBuilder
{
    /// <summary>Adds player-authenticated routes below this feature's <c>/api/x/{id}</c> path.</summary>
    void PlayerRoutes(Action<RouteGroupBuilder> routes);

    /// <summary>Adds unauthenticated routes below this feature's <c>/api/x/{id}</c> path.</summary>
    void OpenRoutes(Action<RouteGroupBuilder> routes);

    /// <summary>Adds administration routes below this feature's <c>/api/admin/x/{id}</c> path.</summary>
    void AdminRoutes(AdminRole minimumRole, Action<RouteGroupBuilder> routes);

    void On<TEvent>(Func<ITenantTx, TEvent, CancellationToken, Task> handler);

    void CleanupPlayer(Func<ITenantTx, Guid, CancellationToken, Task> handler);

    void Job(string kind, Func<JobContext, CancellationToken, Task> handler, WhenDisabled whenDisabled = WhenDisabled.Complete);

    void Every(TimeSpan interval, string kind);

    /// <summary>Adds items to a requested player's inbox.</summary>
    void Inbox(InboxHandler handler);

    /// <summary>Provides a named, typed, read-only query to other declared feature consumers.</summary>
    void Provides<TResult>(string name, NamedQueryHandler<TResult> handler);

    /// <summary>Declares a named query this feature may consume.</summary>
    void Uses(string name);
}

/// <summary>Builds feature-provided inbox items for one player inside the caller's tenant transaction.</summary>
public delegate Task<IReadOnlyList<InboxItem>> InboxHandler(
    ITenantTx transaction,
    Guid playerId,
    CancellationToken cancellationToken);

/// <summary>A feature-owned item to be rendered in a player's inbox.</summary>
public sealed record InboxItem(string Type, JsonElement Payload);

/// <summary>Invokes enabled feature inbox handlers inside a tenant transaction.</summary>
public interface IInboxItems
{
    Task<IReadOnlyList<InboxItem>> GetInboxItemsAsync(
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken);
}

/// <summary>Runs a named, typed, read-only feature query inside the caller's tenant transaction.</summary>
public delegate Task<TResult> NamedQueryHandler<TResult>(
    ITenantTx transaction,
    Guid playerId,
    CancellationToken cancellationToken);

/// <summary>Consumer identities for platform core, which is not a feature but may use a fixed set of named queries.</summary>
public static class NamedQueryConsumers
{
    /// <summary>Platform core. It may use <c>friends.ids</c> for lobby invitation eligibility.</summary>
    public const string Core = "core";
}

/// <summary>Invokes a feature's declared named queries inside the caller's tenant transaction.</summary>
public interface INamedQueries
{
    /// <summary>Invokes the named query declared by the consumer feature.</summary>
    Task<NamedQueryResult<TResult>> InvokeAsync<TResult>(
        string consumerFeatureId,
        string name,
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken);
}

/// <summary>The result of invoking a named feature query.</summary>
public sealed record NamedQueryResult<TResult>(bool Available, TResult? Value)
{
    public static NamedQueryResult<TResult> Unavailable { get; } = new(false, default);
}

/// <summary>The tenant role required to use an administration route.</summary>
public enum AdminRole
{
    Moderator,
    Editor,
    Owner,
}

/// <summary>Marks a player route as available to guest players.</summary>
public sealed class AllowGuestsMetadata
{
    internal AllowGuestsMetadata() { }
}

public static class PlayerRouteConventionBuilderExtensions
{
    /// <summary>Allows a guest player to call this otherwise player-only endpoint.</summary>
    public static TBuilder AllowGuests<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new AllowGuestsMetadata());
        return builder;
    }
}

/// <summary>What to do with a queued feature job whose feature is disabled for its tenant.</summary>
public enum WhenDisabled
{
    Complete,
    Cancel
}

/// <summary>Tenant-bound durable work made available to a registered feature-job handler.</summary>
public sealed class JobContext
{
    public JobContext(long id, Guid? tenantId, string owner, string kind, JsonElement payload, ITenantDb? database)
    {
        Id = id;
        TenantId = tenantId;
        Owner = owner;
        Kind = kind;
        Payload = payload;
        Database = database;
    }

    public long Id { get; }
    public Guid? TenantId { get; }
    public string Owner { get; }
    public string Kind { get; }
    public JsonElement Payload { get; }

    /// <summary>
    /// The tenant-bound database for tenant jobs, or <see langword="null"/> for global jobs.
    /// It accepts only this job's explicit tenant id and has no ambient or fallback tenant.
    /// </summary>
    public ITenantDb? Database { get; }
}
