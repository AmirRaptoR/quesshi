using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Tenant-isolated durable session creation and viewing routes.</summary>
public sealed class SessionEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        routes.PlayerRoutes.MapPost("/sessions", CreateAsync);
        routes.PlayerRoutes.MapGet("/sessions/{id:guid}", GetAsync).AllowGuests();
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateSessionBody body,
        HttpContext context,
        ITenantDb database,
        ISessions sessions,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Session creation requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("Session creation requires player authentication.");
        var request = body.ToRequest();
        if (request is null) return Error("invalid_request", StatusCodes.Status400BadRequest);

        try
        {
            var result = await database.InTenantAsync(tenant.Id, transaction => sessions.CreateAsync(transaction, player.PlayerId, request, cancellationToken), cancellationToken: cancellationToken);
            return result.Status switch
            {
                SessionCreateStatus.Created => Results.Created($"/api/sessions/{result.SessionId}", new { id = result.SessionId, code = result.Code }),
                SessionCreateStatus.Replayed => Results.Ok(new { id = result.SessionId, code = result.Code }),
                SessionCreateStatus.IdConflict => Error("id_conflict", StatusCodes.Status409Conflict),
                SessionCreateStatus.FormatUnavailable => Error("format_unavailable", StatusCodes.Status409Conflict),
                SessionCreateStatus.SourceUnavailable => Error("source_unavailable", StatusCodes.Status503ServiceUnavailable),
                SessionCreateStatus.NotEnoughQuestions => Error("not_enough_questions", StatusCodes.Status422UnprocessableEntity),
                _ => Error(result.Error ?? "invalid_request", StatusCodes.Status400BadRequest),
            };
        }
        catch (NotEnoughQuestionsException) { return Error("not_enough_questions", StatusCodes.Status422UnprocessableEntity); }
        catch (SourceUnavailableException) { return Error("source_unavailable", StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext context,
        ISessionSnapshotLoader snapshots,
        IFormatRegistry formats,
        IViewRenderer renderer,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("Session viewing requires tenant resolution.");
        var player = context.Player() ?? throw new InvalidOperationException("Session viewing requires player authentication.");
        var state = await snapshots.LoadAsync(tenant.Id, id, cancellationToken);
        if (state is null || !state.Seats.ContainsKey(player.PlayerId.ToString()) || state.Session.Format is null || !formats.TryGet(state.Session.Format, out var format))
            return Error("not_found", StatusCodes.Status404NotFound);
        var view = renderer.Render(state, format, player.PlayerId.ToString(), clock.Now);
        return Results.Content(view.Json, "application/json");
    }

    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);

    public sealed record CreateSessionBody(Guid Id, string? Format, CreateContentBody? Content, JsonElement Rules, int Capacity, string? Shape, IReadOnlyList<CreateParticipantBody>? Participants)
    {
        public SessionCreateRequest? ToRequest()
        {
            if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Format) || Content is null || string.IsNullOrWhiteSpace(Content.Source)
                || Rules.ValueKind != JsonValueKind.Object || Content.Parameters.ValueKind != JsonValueKind.Object || !Enum.TryParse<SessionShape>(Shape, true, out var shape))
                return null;
            return new SessionCreateRequest(Id, Format, new SessionContent(Content.Source, Content.Parameters, Content.Count), Rules, Capacity, shape,
                Participants?.Select(participant => new SessionParticipant(participant.PlayerId, participant.Role ?? "player")).ToArray());
        }
    }

    public sealed record CreateContentBody(string? Source, JsonElement Parameters, int Count);
    public sealed record CreateParticipantBody(Guid PlayerId, string? Role);
}
