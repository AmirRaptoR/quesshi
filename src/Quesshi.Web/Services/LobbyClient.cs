using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Wraps the app shell's one connection to <c>/hub/lobby</c>, held for the whole session (#25) —
/// this issue adds the challenge events and calls. One instance, started once, so an invitation
/// arrives wherever the player is in the app rather than on one page.
/// </summary>
public sealed class LobbyClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly HubConnection _connection;

    public event Action<LiveChallengeDto>? ChallengeReceived;
    public event Action<string>? ChallengeExpired;
    public event Action<string>? ChallengeDeclined;
    public event Action<string>? DuelReady;
    public event Action<string>? ChallengeFailed;

    public LobbyClient(string hubUrl, Func<string?> accessTokenProvider)
        : this(BuildConnection(hubUrl, accessTokenProvider))
    {
    }

    internal LobbyClient(HubConnection connection)
    {
        _connection = connection;

        _connection.On<LiveChallengeDto>("ChallengeReceived", c => ChallengeReceived?.Invoke(c));
        _connection.On<string>("ChallengeExpired", id => ChallengeExpired?.Invoke(id));
        _connection.On<string>("ChallengeDeclined", id => ChallengeDeclined?.Invoke(id));
        _connection.On<string>("DuelReady", matchId => DuelReady?.Invoke(matchId));
        _connection.On<string>("ChallengeFailed", id => ChallengeFailed?.Invoke(id));
    }

    internal HubConnection Connection => _connection;

    private static HubConnection BuildConnection(string hubUrl, Func<string?> accessTokenProvider)
        => new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult(accessTokenProvider()))
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    public Task<int> ChallengeAsync(string targetId, string? lang, int questionCount, List<string> categoryIds, List<int> levels, CancellationToken ct = default)
        => _connection.InvokeAsync<int>("Challenge", targetId, lang, questionCount, categoryIds, levels, ct);

    public Task<LiveChallengeAcceptResultDto> AcceptAsync(string challengeId, CancellationToken ct = default)
        => _connection.InvokeAsync<LiveChallengeAcceptResultDto>("Accept", challengeId, ct);

    public Task<int> DeclineAsync(string challengeId, CancellationToken ct = default)
        => _connection.InvokeAsync<int>("Decline", challengeId, ct);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>The wire shape of <c>LobbyHub.Accept</c>'s return value.</summary>
public sealed record LiveChallengeAcceptResultDto(int Result, string? MatchId, string? ChallengerId);
