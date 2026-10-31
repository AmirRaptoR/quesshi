using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>A versioned inbox snapshot exactly as the server sent it; pages read what they need from <see cref="Data"/>.</summary>
public sealed record InboxState(long Version, JsonElement Data);

/// <summary>The current effects of one session, ordered by the server's revision.</summary>
public sealed record EffectsState(long Revision, JsonElement Data);

/// <summary>
/// Shell-scoped snapshot state. Stream messages and command responses feed the same accept methods,
/// and every version only advances forward, so whichever copy of a snapshot arrives late is dropped.
/// </summary>
public sealed class PlatformState : IDisposable
{
    private readonly AppConfig config;
    private readonly StreamClient? stream;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private long configVersion = long.MinValue;

    public PlatformState(AppConfig config, StreamClient? stream = null, TimeProvider? timeProvider = null)
    {
        this.config = config;
        this.stream = stream;
        time = timeProvider ?? TimeProvider.System;
        if (stream is not null) stream.Received += OnReceived;
    }

    public SessionView? View { get; private set; }
    public InboxState? Inbox { get; private set; }
    public EffectsState? Effects { get; private set; }

    /// <summary>Server time minus browser time, measured when the current view arrived.</summary>
    public TimeSpan ClockOffset { get; private set; }

    /// <summary>Raised after a newer view is accepted, with the view it replaced (null for the first).</summary>
    public event Action<SessionView?, SessionView>? ViewChanged;
    public event Action<InboxState>? InboxChanged;
    public event Action<EffectsState>? EffectsChanged;

    /// <summary>Accepts a view from the stream or a command response; false when it is not newer than the current one.</summary>
    public bool AcceptView(SessionView view)
    {
        SessionView? previous;
        lock (gate)
        {
            previous = View;
            if (previous is not null && previous.Id == view.Id && view.Version <= previous.Version) return false;
            View = view;
            ClockOffset = view.Now - time.GetUtcNow();
        }

        ViewChanged?.Invoke(previous, view);
        return true;
    }

    public bool AcceptInbox(InboxState inbox)
    {
        lock (gate)
        {
            if (Inbox is { } current && inbox.Version <= current.Version) return false;
            Inbox = inbox;
        }

        InboxChanged?.Invoke(inbox);
        return true;
    }

    public bool AcceptEffects(EffectsState effects)
    {
        lock (gate)
        {
            if (Effects is { } current && effects.Revision <= current.Revision) return false;
            Effects = effects;
        }

        EffectsChanged?.Invoke(effects);
        return true;
    }

    /// <summary>Refreshes the tenant configuration through <see cref="AppConfig"/> when the version is newer than any seen.</summary>
    public async Task<bool> AcceptConfigAsync(long version, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (version <= configVersion || version <= config.Current.ConfigVersion) return false;
            configVersion = version;
        }

        await config.ConfigChanged(version, cancellationToken);
        return true;
    }

    /// <summary>Seconds left until the current view's server deadline, on the server's clock; null when there is no deadline.</summary>
    public int? RemainingSeconds()
    {
        SessionView? view;
        TimeSpan offset;
        lock (gate) (view, offset) = (View, ClockOffset);
        return view?.Deadline is { } deadline ? RingClock.RemainingSeconds(deadline, time.GetUtcNow(), offset) : null;
    }

    private void OnReceived(StreamNotification notification)
    {
        try
        {
            using var document = JsonDocument.Parse(notification.Data);
            var root = document.RootElement.Clone();
            switch (notification.Kind)
            {
                case StreamNotificationKind.View:
                    if (root.Deserialize<SessionView>(JsonOptions) is { } view) AcceptView(view);
                    break;
                case StreamNotificationKind.Inbox:
                    if (Number(root, "version") is { } inboxVersion) AcceptInbox(new InboxState(inboxVersion, root));
                    break;
                case StreamNotificationKind.Effects:
                    if (Number(root, "revision") is { } revision) AcceptEffects(new EffectsState(revision, root));
                    break;
                case StreamNotificationKind.Config:
                    if (Number(root, "version") is { } version) _ = AcceptConfigQuietlyAsync(version);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private async Task AcceptConfigQuietlyAsync(long version)
    {
        try { await AcceptConfigAsync(version); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or Microsoft.JSInterop.JSException)
        {
            lock (gate) if (configVersion == version) configVersion = long.MinValue;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new RosterViewConverter() } };

    /// <summary>Reads a SessionView as the server writes it (stream frames and command responses).</summary>
    public static SessionView? ParseView(string json) => JsonSerializer.Deserialize<SessionView>(json, JsonOptions);

    private static long? Number(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    public void Dispose()
    {
        if (stream is not null) stream.Received -= OnReceived;
    }
}
