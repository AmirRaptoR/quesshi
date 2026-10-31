using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>Builds lobby <see cref="SessionView"/>s the way the server writes them, through the client's own parser.</summary>
internal static class LobbyViews
{
    public const string SessionId = "6b0d5a0e-1d2c-4a52-9a43-0f3a7e1c9b11";

    public static string Json(string meId, string[] players, string status = "lobby", string format = "fake", string code = "ABCD23", bool[]? online = null, long version = 1)
    {
        var seats = string.Join(",", players.Select((id, index) =>
            $$$"""{"id":"{{{id}}}","name":"{{{id}}}","seat":{{{index}}},"role":"player","status":"active","score":0,"place":null,"outcome":null,"online":{{{(online?[index] ?? false ? "true" : "false")}}}}"""));
        var me = Array.IndexOf(players, meId);
        return $$$"""{"id":"{{{SessionId}}}","code":"{{{code}}}","format":"{{{format}}}","status":"{{{status}}}","phase":"idle","slot":-1,"step":0,"version":{{{version}}},"configVersion":1,"now":"2026-01-01T00:00:00Z","deadline":null,"me":{"id":"{{{meId}}}","seat":{{{Math.Max(me, 0)}}},"role":"player","status":"active","score":0,"place":null,"outcome":null},"roster":{"total":{{{players.Length}}},"seats":[{{{seats}}}]},"content":{},"rules":{},"body":{}}""";
    }

    public static SessionView Parse(string meId, string[] players, string status = "lobby", string format = "fake", string code = "ABCD23", bool[]? online = null, long version = 1) =>
        PlatformState.ParseView(Json(meId, players, status, format, code, online, version))!;
}
