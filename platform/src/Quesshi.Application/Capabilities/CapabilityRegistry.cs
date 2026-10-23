using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Application;

internal enum CapabilityType
{
    Kind,
    Format,
    Feature,
    Signin,
    Source,
    Switch
}

/// <summary>Defines the JSON contract shared by stored capability settings and their consumers.</summary>
internal static class CapabilitySettingsJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

internal abstract class CapabilityDefinition
{
    protected CapabilityDefinition(CapabilityType type, string key, Type settingsType)
    {
        Type = type;
        Key = key;
        SettingsType = settingsType;
    }

    public CapabilityType Type { get; }

    public string Key { get; }

    public Type SettingsType { get; }

    public abstract object DefaultSettingsObject { get; }

    public abstract IReadOnlyList<string> ValidateObject(object settings);

    public abstract object Deserialize(JsonElement settings);

    public abstract JsonElement SerializeObject(object settings);
}

internal sealed class CapabilityDefinition<TSettings>(
    CapabilityType type,
    string key,
    TSettings defaultSettings,
    Func<TSettings, IReadOnlyList<string>> validator) : CapabilityDefinition(type, key, typeof(TSettings))
    where TSettings : notnull
{
    public TSettings DefaultSettings { get; } = defaultSettings;

    public override object DefaultSettingsObject => DefaultSettings;

    public IReadOnlyList<string> Validate(TSettings settings) => validator(settings);

    public override IReadOnlyList<string> ValidateObject(object settings) =>
        settings is TSettings typed ? Validate(typed) : ["Settings have the wrong type."];

    public override object Deserialize(JsonElement settings) =>
        JsonSerializer.Deserialize<TSettings>(settings, CapabilitySettingsJson.Options) ?? throw new InvalidOperationException($"Capability {Type}:{Key} has invalid settings.");

    public override JsonElement SerializeObject(object settings) =>
        settings is TSettings typed
            ? JsonSerializer.SerializeToElement(typed, CapabilitySettingsJson.Options)
            : throw new InvalidOperationException($"Capability {Type}:{Key} requires {typeof(TSettings).Name} settings.");
}

internal sealed class FeatureCapabilityDefinition(CapabilityType type, string key, FeatureSettings settings) : CapabilityDefinition(type, key, settings.SettingsType)
{
    public override object DefaultSettingsObject => settings.DefaultSettings;

    public override IReadOnlyList<string> ValidateObject(object value) => settings.Validate(value);

    public override object Deserialize(JsonElement value) =>
        JsonSerializer.Deserialize(value, SettingsType, CapabilitySettingsJson.Options) ?? throw new InvalidOperationException($"Capability {Type}:{Key} has invalid settings.");

    public override JsonElement SerializeObject(object value)
    {
        var validationErrors = settings.Validate(value);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException($"Capability {Type}:{Key} has invalid settings: {string.Join(" ", validationErrors)}");
        }

        return JsonSerializer.SerializeToElement(value, SettingsType, CapabilitySettingsJson.Options);
    }
}

internal sealed class CapabilityRegistry
{
    private readonly Dictionary<(CapabilityType Type, string Key), CapabilityDefinition> _definitions = [];

    public IReadOnlyCollection<CapabilityDefinition> Definitions => _definitions.Values;

    public CapabilityDefinition<TSettings> Register<TSettings>(
        CapabilityType type,
        string key,
        TSettings defaultSettings,
        Func<TSettings, IReadOnlyList<string>> validator)
        where TSettings : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(defaultSettings);
        ArgumentNullException.ThrowIfNull(validator);
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));

        var normalizedKey = key.Trim();
        var definition = new CapabilityDefinition<TSettings>(type, normalizedKey, defaultSettings, validator);
        if (!_definitions.TryAdd((type, normalizedKey), definition))
        {
            throw new InvalidOperationException($"Capability {type}:{normalizedKey} is already registered.");
        }

        return definition;
    }

    public CapabilityDefinition Register(CapabilityType type, string key, FeatureSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));

        var normalizedKey = key.Trim();
        var definition = new FeatureCapabilityDefinition(type, normalizedKey, settings);
        if (!_definitions.TryAdd((type, normalizedKey), definition))
        {
            throw new InvalidOperationException($"Capability {type}:{normalizedKey} is already registered.");
        }

        return definition;
    }

    public CapabilityDefinition Get(CapabilityType type, string key) =>
        TryGet(type, key) ?? throw new InvalidOperationException($"Capability {type}:{key} is not registered.");

    public CapabilityDefinition? TryGet(CapabilityType type, string key) =>
        _definitions.GetValueOrDefault((type, key));
}

internal sealed record GuestsSettings;

internal sealed record CustomQuestionsSettings;
internal sealed record SessionSettings(int Capacity = 20);

internal static class CoreCapabilities
{
    public static CapabilityRegistry CreateRegistry()
    {
        var registry = new CapabilityRegistry();
        registry.Register(CapabilityType.Switch, "guests", new GuestsSettings(), _ => []);
        registry.Register(CapabilityType.Switch, "custom_questions", new CustomQuestionsSettings(), _ => []);
        registry.Register(CapabilityType.Switch, "sessions", new SessionSettings(), settings => settings.Capacity is >= 1 and <= 500 ? [] : ["Capacity must be between 1 and 500."]);
        return registry;
    }
}
