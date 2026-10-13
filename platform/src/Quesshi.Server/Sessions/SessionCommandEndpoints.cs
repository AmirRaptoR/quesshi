using System.Text;
using System.Text.Json;
using Orleans;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;
using Quesshi.Shared;

namespace Quesshi.Server.Sessions;

internal static class SessionCommandEndpoints
{
    private const int MaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapSessionCommands(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/sessions/{id:guid}/commands", ExecuteAsync).AllowGuests();
        endpoints.MapGet("/sessions/{id:guid}/roster", RosterAsync);
        return endpoints;
    }

    private static async Task<IResult> RosterAsync(HttpContext context, Guid id, [FromServices] ISessionSnapshotLoader snapshots, int? after, int? take, CancellationToken cancellationToken)
    {
        var player = TestPlayer(context);
        if (player is null) return Error("identity_required", StatusCodes.Status401Unauthorized);
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Error("not_found", StatusCodes.Status404NotFound);
        if (after is < 0 || take is <= 0) return Error("invalid_page", StatusCodes.Status400BadRequest);
        var page = await snapshots.LoadRosterPageAsync(tenant.Id, id, player, after, take ?? 50, cancellationToken);
        return page is null ? Error("not_found", StatusCodes.Status404NotFound) : Results.Json(page);
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, Guid id, IGrainFactory grains, IClock clock, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > MaxBodyBytes) return Error("body_too_large", StatusCodes.Status413PayloadTooLarge);
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (body is null) return Error("body_too_large", StatusCodes.Status413PayloadTooLarge);

        // Receipt is deliberately sampled immediately after the complete bounded
        // request body arrives; parsing and identity resolution cannot extend it.
        var receipt = clock.Now;
        CommandDocument? document;
        try { document = JsonSerializer.Deserialize<CommandDocument>(body, JsonOptions); }
        catch (JsonException) { return Error("malformed_command", StatusCodes.Status400BadRequest); }
        if (document is null || string.IsNullOrWhiteSpace(document.Type) || document.At is null) return Error("malformed_command", StatusCodes.Status400BadRequest);
        var player = TestPlayer(context);
        if (player is null) return Error("identity_required", StatusCodes.Status401Unauthorized);
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Error("not_found", StatusCodes.Status404NotFound);

        var command = new SessionCommand(player, document.Type, document.At.Phase ?? "", document.At.Slot, document.At.Step,
            document.Revision, document.Name, document.Payload?.GetRawText(), receipt);
        var result = await grains.GetGrain<ISessionGrain>($"{tenant.Id}/{id}").ExecuteAsync(command);
        if (result.ViewJson is not null) return Results.Content(result.ViewJson, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
        return Error(result.Code, Status(result.Code));
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var count = await request.Body.ReadAsync(bytes, cancellationToken);
            if (count == 0) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (buffer.Length + count > MaxBodyBytes) return null;
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }
    }

    private static string? TestPlayer(HttpContext context)
    {
#if QUESSHI_TEST_IDENTITY
        return context.Request.Headers.TryGetValue("X-Quesshi-Test-Player", out var value) && Guid.TryParse(value, out var id) ? id.ToString() : null;
#else
        return context.Player()?.PlayerId.ToString();
#endif
    }
    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    private static int Status(string code) => code switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "absent_seat" => StatusCodes.Status403Forbidden,
        "session_busy" or "stale_activation" or "retryable" => StatusCodes.Status409Conflict,
        "late_answer" => StatusCodes.Status409Conflict,
        "invalid_command" or "revision_conflict" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record CommandDocument(string? Type, PositionDocument? At, int? Revision, string? Name, JsonElement? Payload);
    private sealed record PositionDocument(string? Phase, int Slot, int Step);
}
