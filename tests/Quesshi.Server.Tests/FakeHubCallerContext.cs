using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// A minimal <see cref="HubCallerContext"/> for driving a hub method directly, with no connection at
/// all — proves a guest refusal without standing up a socket.
/// </summary>
public sealed class FakeHubCallerContext(string playerId, bool isGuest) : HubCallerContext
{
    public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public override string? UserIdentifier => playerId;
    public override ClaimsPrincipal User { get; } = BuildUser(playerId, isGuest);
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }

    private static ClaimsPrincipal BuildUser(string playerId, bool isGuest)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, playerId) };
        if (isGuest) claims.Add(new Claim(TokenIssuer.GuestClaim, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
