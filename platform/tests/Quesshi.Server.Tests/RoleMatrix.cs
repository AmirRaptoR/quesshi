using System.Net;

namespace Quesshi.Server.Tests;

/// <summary>Runs the same request as every supported HTTP identity and reports the mismatched role.</summary>
internal sealed class RoleMatrix(IEnumerable<RoleMatrixExpectation> expectations)
{
    public static RoleMatrix ForEditorOnlyMutation(PlatformHttpFixture host) => new(
    [
        new("anonymous", host.Anonymous, HttpStatusCode.Unauthorized),
        new("guest", host.Guest, HttpStatusCode.Unauthorized),
        new("player", host.Player, HttpStatusCode.Unauthorized),
        new("tenant moderator", host.Moderator, HttpStatusCode.Forbidden),
        new("editor", host.Editor, HttpStatusCode.OK),
        new("owner", host.Owner, HttpStatusCode.OK),
        new("platform administrator", host.PlatformAdministrator, HttpStatusCode.OK),
    ]);

    public static RoleMatrix ForOwnerOnlyMutation(PlatformHttpFixture host) => new(
    [
        new("anonymous", host.Anonymous, HttpStatusCode.Unauthorized),
        new("guest", host.Guest, HttpStatusCode.Unauthorized),
        new("player", host.Player, HttpStatusCode.Unauthorized),
        new("tenant moderator", host.Moderator, HttpStatusCode.Forbidden),
        new("editor", host.Editor, HttpStatusCode.Forbidden),
        new("owner", host.Owner, HttpStatusCode.OK),
        new("platform administrator", host.PlatformAdministrator, HttpStatusCode.OK),
    ]);

    public static RoleMatrix ForOwnerOnlyRead(PlatformHttpFixture host) => new(

    [
        new("anonymous", host.Anonymous, HttpStatusCode.Unauthorized),
        new("guest", host.Guest, HttpStatusCode.Unauthorized),
        new("player", host.Player, HttpStatusCode.Unauthorized),
        new("tenant moderator", host.Moderator, HttpStatusCode.Forbidden),
        new("editor", host.Editor, HttpStatusCode.Forbidden),
        new("owner", host.Owner, HttpStatusCode.OK),
        new("platform administrator", host.PlatformAdministrator, HttpStatusCode.OK),
    ]);

    public static RoleMatrix ForOwnerOnlyNoContentMutation(PlatformHttpFixture host) => new(
    [
        new("anonymous", host.Anonymous, HttpStatusCode.Unauthorized),
        new("guest", host.Guest, HttpStatusCode.Unauthorized),
        new("player", host.Player, HttpStatusCode.Unauthorized),
        new("tenant moderator", host.Moderator, HttpStatusCode.Forbidden),
        new("editor", host.Editor, HttpStatusCode.Forbidden),
        new("owner", host.Owner, HttpStatusCode.NoContent),
        new("platform administrator", host.PlatformAdministrator, HttpStatusCode.NoContent),
    ]);

    public async Task AssertAsync(Func<HttpClient, Task<HttpResponseMessage>> sendAsync)
    {
        foreach (var expectation in expectations)
        {
            using var response = await sendAsync(expectation.Client);
            Assert.True(
                response.StatusCode == expectation.ExpectedStatus,
                $"Role '{expectation.Role}' expected HTTP {(int)expectation.ExpectedStatus} ({expectation.ExpectedStatus}) but observed HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }
    }
}

internal sealed record RoleMatrixExpectation(string Role, HttpClient Client, HttpStatusCode ExpectedStatus);
