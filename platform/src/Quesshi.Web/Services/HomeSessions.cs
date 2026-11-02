using System.Text.Json;

namespace Quesshi.Web.Services;

/// <summary>A session the inbox lists for the player: where to open it, the code others join with and its format.</summary>
public sealed record HomeSession(Guid Id, string Code, string Format);

/// <summary>An invitation the player can still accept or decline.</summary>
public sealed record HomeInvitation(Guid SessionId, Guid InvitedBy);

/// <summary>What Home shows from the inbox: invitations, lobbies the player sits in and sessions waiting for their move.</summary>
public sealed record HomeInbox(IReadOnlyList<HomeInvitation> Invitations, IReadOnlyList<HomeSession> Lobbies, IReadOnlyList<HomeSession> Turns, IReadOnlyList<string> Waiting)
{
    /// <summary>The inbox item type the matchmaking feature contributes while a player looks for an opponent.</summary>
    public const string WaitingItemType = "matchmaking.waiting";

    public static HomeInbox Empty { get; } = new([], [], [], []);

    public static HomeInbox From(JsonElement data) => new(
        Items(data, "invitations", item => Guid.TryParse(Text(item, "sessionId"), out var session) && Guid.TryParse(Text(item, "invitedBy"), out var by)
            ? new HomeInvitation(session, by) : null),
        Items(data, "lobbies", Session),
        Items(data, "turns", Session),
        Items(data, "items", WaitingFormat).Select(format => format.Value).ToArray());

    private static StringBox? WaitingFormat(JsonElement item) =>
        Text(item, "type") == WaitingItemType && item.TryGetProperty("payload", out var payload) && Text(payload, "format") is { Length: > 0 } format
            ? new StringBox(format) : null;

    private sealed record StringBox(string Value);

    private static HomeSession? Session(JsonElement item) =>
        Guid.TryParse(Text(item, "id"), out var id) ? new HomeSession(id, Text(item, "code") ?? "", Text(item, "format") ?? "") : null;

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<T> Items<T>(JsonElement data, string name, Func<JsonElement, T?> read) where T : class =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(read).OfType<T>().ToArray()
            : [];
}

/// <summary>Result of an action that ends by opening a session: its id on success, otherwise a stable error code.</summary>
public sealed record HomeOutcome(Guid? SessionId, string? ErrorCode)
{
    public bool Succeeded => SessionId is not null;
    public static HomeOutcome Opened(Guid id) => new(id, null);
    public static HomeOutcome Failed(string code) => new(null, code);
}

/// <summary>
/// The server calls behind Home: creating a session, joining by code, reading the inbox and answering
/// invitations. Every success that seats the player resolves to the session id so Home can open <c>/s/{id}</c>.
/// </summary>
public sealed class HomeSessions(PlayerApi api, PlatformState state, AppConfig config)
{
    /// <summary>Questions a new session starts with; the lobby's settings change it before the session starts.</summary>
    public const int DefaultCount = 10;

    /// <summary>Seats a new session starts with; the lobby's settings change it before the session starts.</summary>
    public const int DefaultCapacity = 2;

    /// <summary>Formats the tenant has enabled that this client can also render.</summary>
    public IReadOnlyList<string> EnabledFormats(Func<string, bool> isRegistered) =>
        config.Current.Capabilities.TryGetValue("format", out var formats) ? formats.Where(isRegistered).ToArray() : [];

    public async Task<HomeOutcome> CreateAsync(string format, CancellationToken cancellationToken = default)
    {
        if (!config.Current.Capabilities.TryGetValue("source", out var sources) || sources.Length == 0) return HomeOutcome.Failed("source_unavailable");
        var id = Guid.NewGuid();
        try
        {
            var response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, "/api/sessions", new
            {
                id,
                format,
                content = new { source = sources[0], parameters = new { }, count = DefaultCount },
                rules = new { },
                capacity = DefaultCapacity,
            }, cancellationToken);
            return response.IsSuccess ? HomeOutcome.Opened(id) : HomeOutcome.Failed(response.Error ?? "request_failed");
        }
        catch (HttpRequestException) { return HomeOutcome.Failed("network_error"); }
    }

    public async Task<HomeOutcome> JoinByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (code.Length == 0) return HomeOutcome.Failed("invalid_request");
        try
        {
            var response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, "/api/sessions/join", new { code }, cancellationToken);
            if (!response.IsSuccess) return HomeOutcome.Failed(response.Error ?? "request_failed");

            // The join response carries no session id, so the seat the server just gave is read back from the inbox.
            var inbox = await LoadInboxAsync(cancellationToken);
            var joined = inbox.Lobbies.Concat(inbox.Turns).FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
            return joined is null ? HomeOutcome.Failed("not_found") : HomeOutcome.Opened(joined.Id);
        }
        catch (HttpRequestException) { return HomeOutcome.Failed("network_error"); }
    }

    public async Task<HomeInbox> LoadInboxAsync(CancellationToken cancellationToken = default)
    {
        var response = await api.GetAsync<JsonElement>("/api/me/inbox", cancellationToken);
        if (!response.IsSuccess) return HomeInbox.Empty;
        if (response.Value.ValueKind == JsonValueKind.Object && response.Value.TryGetProperty("version", out var version) && version.TryGetInt64(out var number))
            state.AcceptInbox(new InboxState(number, response.Value.Clone()));
        return HomeInbox.From(response.Value);
    }

    /// <summary>Whether the tenant has the matchmaking feature on.</summary>
    public bool MatchmakingEnabled =>
        config.Current.Capabilities.TryGetValue("feature", out var features) && features.Contains("matchmaking", StringComparer.Ordinal);

    /// <summary>Joins the format's matchmaking queue; returns null on success, else the server's stable error code.</summary>
    public async Task<string?> JoinQueueAsync(string format, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, "/api/x/matchmaking",
                new { format, settings = new { questionCount = DefaultCount } }, cancellationToken);
            return response.IsSuccess ? null : response.Error ?? "request_failed";
        }
        catch (HttpRequestException) { return "network_error"; }
    }

    /// <summary>Leaves the format's matchmaking queue; returns null on success, else the server's stable error code.</summary>
    public async Task<string?> LeaveQueueAsync(string format, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await api.SendMutationAsync<JsonElement>(HttpMethod.Delete, $"/api/x/matchmaking/{Uri.EscapeDataString(format)}", null, cancellationToken);
            return response.IsSuccess ? null : response.Error ?? "request_failed";
        }
        catch (HttpRequestException) { return "network_error"; }
    }

    public async Task<HomeOutcome> AcceptAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, $"/api/sessions/{sessionId}/invites/accept", null, cancellationToken);
            return response.IsSuccess ? HomeOutcome.Opened(sessionId) : HomeOutcome.Failed(response.Error ?? "request_failed");
        }
        catch (HttpRequestException) { return HomeOutcome.Failed("network_error"); }
    }

    public async Task<bool> DeclineAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await api.SendMutationAsync<JsonElement>(HttpMethod.Post, $"/api/sessions/{sessionId}/invites/decline", null, cancellationToken);
            return response.IsSuccess;
        }
        catch (HttpRequestException) { return false; }
    }
}

/// <summary>
/// Where a random-opponent search stands. It starts when the player asks for one (or the inbox already shows them waiting)
/// and ends when they cancel or the inbox shows the pairing's session.
/// </summary>
public sealed class HomeSearch
{
    private HashSet<Guid> known = [];
    private bool sawWaiting;

    /// <summary>The format being searched for, or null when Home is in its normal state.</summary>
    public string? Format { get; private set; }

    public void Start(string format, HomeInbox inbox)
    {
        Format = format;
        sawWaiting = false;
        known = Sessions(inbox, format).Select(s => s.Id).ToHashSet();
    }

    public void Cancel() => Format = null;

    /// <summary>Folds an inbox update in; returns the session to open once the search has been paired.</summary>
    public Guid? Observe(HomeInbox inbox)
    {
        if (Format is null)
        {
            // A reload while queued: the inbox is the source of truth for the waiting state.
            if (inbox.Waiting.FirstOrDefault() is { } waiting)
            {
                Start(waiting, inbox);
                sawWaiting = true;
            }

            return null;
        }

        if (inbox.Waiting.Contains(Format, StringComparer.Ordinal))
        {
            sawWaiting = true;
            return null;
        }

        var sessions = Sessions(inbox, Format);
        var paired = sessions.FirstOrDefault(s => !known.Contains(s.Id)) ?? (sawWaiting ? sessions.FirstOrDefault() : null);
        if (paired is null) return null;
        Format = null;
        return paired.Id;
    }

    private static IEnumerable<HomeSession> Sessions(HomeInbox inbox, string format) =>
        inbox.Lobbies.Concat(inbox.Turns).Where(s => string.Equals(s.Format, format, StringComparison.Ordinal));
}
