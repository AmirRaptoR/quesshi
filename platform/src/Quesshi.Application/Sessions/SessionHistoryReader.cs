using System.Globalization;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

public enum SessionQuestionHistoryStatus
{
    Found,
    NotFound,
    NotFinished,
}

public sealed record SessionQuestionHistoryResult(SessionQuestionHistoryStatus Status, SessionQuestionHistoryPage? Page);

/// <summary>
/// Reads a player's session history from the persisted rows only (<c>session_participants</c>, <c>session_items</c>,
/// <c>session_answers</c>); the grain is never consulted. Every read runs in a tenant transaction.
/// </summary>
public sealed class SessionHistoryReader(ITenantDb database)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    /// <summary>
    /// The next page of the player's sessions ordered by <c>(joined_at desc, session id)</c>. Returns null when
    /// <paramref name="before"/> is not a cursor this reader issued.
    /// </summary>
    public async Task<SessionHistoryPage?> ReadSessionsAsync(
        Guid tenantId,
        Guid playerId,
        string? before,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var size = ClampLimit(limit);
        var cursor = default((DateTime JoinedAt, Guid SessionId)?);
        if (before is not null)
        {
            if (!TryParseCursor(before, out var parsed)) return null;
            cursor = parsed;
        }

        var rows = await database.InTenantAsync(tenantId, transaction => transaction.QueryAsync<SessionRow>("""
            select p.session_id as Id, s.format as Format, s.status as Status, p.joined_at as JoinedAt,
                   p.score as Score, p.place::int as Place, p.outcome as Outcome
            from session_participants p
            join sessions s on s.tenant_id = p.tenant_id and s.id = p.session_id
            where p.player_id = @playerId
              and (@hasCursor = false or p.joined_at < @joinedAt or (p.joined_at = @joinedAt and p.session_id > @sessionId))
            order by p.joined_at desc, p.session_id
            limit @take
            """, new
        {
            playerId,
            hasCursor = cursor is not null,
            joinedAt = cursor?.JoinedAt ?? DateTime.UnixEpoch,
            sessionId = cursor?.SessionId ?? Guid.Empty,
            take = size + 1,
        }, cancellationToken), cancellationToken: cancellationToken, readOnly: true);

        var page = rows.Take(size).ToArray();
        var next = rows.Count > size ? FormatCursor(page[^1]) : null;
        return new SessionHistoryPage(
            page.Select(row => new SessionHistoryEntry(
                row.Id, row.Format, row.Status, new DateTimeOffset(DateTime.SpecifyKind(row.JoinedAt, DateTimeKind.Utc)),
                row.Score, row.Place, row.Outcome)).ToArray(),
            next);
    }

    /// <summary>
    /// The finished session's questions after slot <paramref name="after"/>, for a seated participant only.
    /// A session the caller never sat in is reported as not found, whatever its state.
    /// </summary>
    public Task<SessionQuestionHistoryResult> ReadQuestionsAsync(
        Guid tenantId,
        Guid playerId,
        Guid sessionId,
        int? after,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var size = ClampLimit(limit);
        var afterSlot = after ?? -1;
        return database.InTenantAsync(tenantId, async transaction =>
        {
            var status = await transaction.QuerySingleOrDefaultAsync<string>("""
                select s.status
                from sessions s
                join session_participants p on p.tenant_id = s.tenant_id and p.session_id = s.id
                where s.id = @sessionId and p.player_id = @playerId
                """, new { sessionId, playerId }, cancellationToken);
            if (status is null) return new SessionQuestionHistoryResult(SessionQuestionHistoryStatus.NotFound, null);
            if (status != "finished") return new SessionQuestionHistoryResult(SessionQuestionHistoryStatus.NotFinished, null);

            var items = await transaction.QueryAsync<ItemRow>("""
                select slot as Slot, kind as Kind, kind_version::int as KindVersion, prompt as Prompt,
                       card::text as Card, reveal::text as Reveal
                from session_items
                where session_id = @sessionId and slot > @afterSlot
                order by slot
                limit @take
                """, new { sessionId, afterSlot, take = size + 1 }, cancellationToken);
            var page = items.Take(size).ToArray();
            var answers = page.Length == 0
                ? []
                : await transaction.QueryAsync<AnswerRow>("""
                    select slot as Slot, answer::text as Answer
                    from session_answers
                    where session_id = @sessionId and player_id = @playerId
                      and slot >= @first and slot <= @last and answer is not null
                    order by slot, seq
                    """, new { sessionId, playerId, first = page[0].Slot, last = page[^1].Slot }, cancellationToken);
            var bySlot = answers.ToLookup(answer => answer.Slot, answer => Parse(answer.Answer));
            return new SessionQuestionHistoryResult(SessionQuestionHistoryStatus.Found, new SessionQuestionHistoryPage(
                page.Select(item => new SessionQuestionHistoryItem(
                    item.Slot, item.Kind, item.KindVersion, item.Prompt, Parse(item.Card), Parse(item.Reveal), bySlot[item.Slot].ToArray())).ToArray(),
                items.Count > size ? page[^1].Slot : null));
        }, cancellationToken: cancellationToken, readOnly: true);
    }

    private static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string FormatCursor(SessionRow row) =>
        $"{DateTime.SpecifyKind(row.JoinedAt, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)}.{row.Id:N}";

    private static bool TryParseCursor(string value, out (DateTime JoinedAt, Guid SessionId) cursor)
    {
        cursor = default;
        var parts = value.Split('.');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks is < 0 || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[1], "N", out var id))
            return false;
        cursor = (new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }

    private sealed record SessionRow(Guid Id, string Format, string Status, DateTime JoinedAt, int Score, int? Place, string? Outcome);
    private sealed record ItemRow(int Slot, string Kind, int KindVersion, string Prompt, string Card, string Reveal);
    private sealed record AnswerRow(int Slot, string Answer);
}
