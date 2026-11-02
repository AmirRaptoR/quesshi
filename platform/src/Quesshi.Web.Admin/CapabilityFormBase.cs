using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Quesshi.Web.Admin;

/// <summary>One tenant capability row as the owner capability API lists it.</summary>
public sealed record CapabilityRow(string Type, string Key, bool Entitled, bool Enabled, JsonElement Settings);

/// <summary>Shared state of every named capability form: the row it edits, the saved flag and the validator messages of the last refused save.</summary>
public abstract class CapabilityFormBase : ComponentBase
{
    private CapabilityRow? loaded;

    [Parameter, EditorRequired] public CapabilityRow Row { get; set; } = default!;
    [Parameter] public IReadOnlyList<string> Messages { get; set; } = [];
    [Parameter] public bool Saved { get; set; }
    [Parameter, EditorRequired] public Func<bool, object, Task> Save { get; set; } = default!;

    protected bool Enabled { get; set; }

    /// <summary>Reads the stored settings into the form fields; called whenever a different row value arrives.</summary>
    protected virtual void Load(JsonElement settings) { }

    /// <summary>The settings object sent with the enabled flag on save.</summary>
    protected virtual object BuildSettings() => new { };

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(loaded, Row)) return;
        loaded = Row;
        Enabled = Row.Enabled;
        Load(Row.Settings);
    }

    protected Task SubmitAsync() => Save(Enabled, BuildSettings());

    protected static string? ReadString(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static int? ReadInt(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
