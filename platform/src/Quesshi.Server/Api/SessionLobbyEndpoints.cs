using System.Text.Json;
using Dapper;
using Orleans;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Authenticated lobby-only session operations. The player is always taken from server identity.</summary>
public sealed class SessionLobbyEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        routes.PlayerRoutes.MapPost("/sessions/join", JoinAsync).AllowGuests();
        routes.PlayerRoutes.MapPut("/sessions/{id:guid}/settings", SettingsAsync).AllowGuests();
        routes.PlayerRoutes.MapPost("/sessions/{id:guid}/start", StartAsync).AllowGuests();
        routes.PlayerRoutes.MapPost("/sessions/{id:guid}/leave", LeaveAsync).AllowGuests();
    }

    private static async Task<IResult> JoinAsync(HttpContext context, ITenantDb database, IGrainFactory grains, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync<JoinBody>(context, cancellationToken);
        if (body is null || string.IsNullOrWhiteSpace(body.Code) || body.Code.Length > 64) return Error("invalid_request", 400);
        var player = context.Player();
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (player is null) return Error("identity_required", 401);
        if (tenant is null) return Error("not_found", 404);
        var id = await database.InTenantAsync(tenant.Id, transaction => transaction.QuerySingleOrDefaultAsync<Guid?>(
            "select id from sessions where code = @code", new { code = body.Code.Trim() }, cancellationToken), cancellationToken: cancellationToken);
        if (id is null) return Error("not_found", 404);
        return Result(await grains.GetGrain<ISessionGrain>($"{tenant.Id}/{id}").ExecuteLobbyAsync(new(player.PlayerId.ToString(), "join")));
    }

    private static async Task<IResult> SettingsAsync(Guid id, HttpContext context, IGrainFactory grains, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync<SettingsBody>(context, cancellationToken);
        if (body is null) return Error("invalid_request", 400);
        return await ExecuteAsync(id, new LobbyCommand("", "settings", body.Rules?.GetRawText(), body.Content?.GetRawText(), body.Capacity), context, grains);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context, CancellationToken cancellationToken) where T : class
    {
        try { return await context.Request.ReadFromJsonAsync<T>(cancellationToken); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or BadHttpRequestException) { return null; }
    }

    private static Task<IResult> StartAsync(Guid id, HttpContext context, IGrainFactory grains) =>
        ExecuteAsync(id, new LobbyCommand(context.Player()?.PlayerId.ToString() ?? "", "start"), context, grains);

    private static Task<IResult> LeaveAsync(Guid id, HttpContext context, IGrainFactory grains) =>
        ExecuteAsync(id, new LobbyCommand(context.Player()?.PlayerId.ToString() ?? "", "leave"), context, grains);

    private static async Task<IResult> ExecuteAsync(Guid id, LobbyCommand? command, HttpContext context, IGrainFactory grains)
    {
        var player = context.Player();
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (player is null) return Error("identity_required", 401);
        if (tenant is null || command is null) return Error("not_found", 404);
        var result = await grains.GetGrain<ISessionGrain>($"{tenant.Id}/{id}").ExecuteLobbyAsync(command with { Player = player.PlayerId.ToString() });
        return Result(result);
    }

    private static IResult Result(LobbyCommandResult result) => result.Code switch
    {
        "joined" or "configured" or "left" or "ended" or "started" => Results.Json(new { code = result.Code, version = result.Version }),
        "not_found" => Error(result.Code, 404),
        "not_owner" or "absent_seat" or "guest_not_allowed" => Error(result.Code, 403),
        "session_full" or "late_join_not_allowed" or "expired" or "cancelled" or "not_in_lobby" or "not_enough_players" => Error(result.Code, 409),
        "source_unavailable" => Error(result.Code, 503),
        "not_enough_questions" => Error(result.Code, 422),
        _ => Error(result.Code, 400),
    };
    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);

    public sealed record JoinBody(string? Code);
    public sealed record SettingsBody(JsonElement? Rules, JsonElement? Content, int? Capacity);
}
