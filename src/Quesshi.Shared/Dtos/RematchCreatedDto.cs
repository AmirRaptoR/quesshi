namespace Quesshi.Shared;

/// <summary>
/// Pushed to a finished duel's own group once a rematch lobby exists for it. <see cref="NewMatchCode"/>
/// is what makes this reach a guest participant: guests never connect to <c>/hub/lobby</c> and so can
/// never receive an in-app invitation (see <c>LobbyHub.OnConnectedAsync</c>'s own remarks), but they
/// are already connected to this duel's own group, so this push is the one place a guest ever learns
/// the rematch lobby's share code — the "link" they are invited by.
/// </summary>
public sealed record RematchCreatedDto(string NewMatchId, string NewMatchCode = "");
