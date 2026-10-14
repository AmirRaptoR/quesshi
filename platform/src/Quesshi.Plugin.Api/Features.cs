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

    void On<TEvent>(Func<ITenantTx, TEvent, CancellationToken, Task> handler);

    void CleanupPlayer(Func<ITenantTx, Guid, CancellationToken, Task> handler);

    void Job(string kind, Func<JobContext, CancellationToken, Task> handler, WhenDisabled whenDisabled = WhenDisabled.Complete);

    void Every(TimeSpan interval, string kind);
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
