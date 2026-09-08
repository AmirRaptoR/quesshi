using Orleans;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

/// <summary>
/// The live equivalent of <c>MatchGrain.SettleAsync</c>: everything that happens once, when a live
/// duel ends — history, stats, the leaderboard, the escalating abandonment penalty. Kept apart from
/// any grain, unlike <c>MatchGrain</c>'s own private method, because the grain that will own the round
/// clock (issue #10) has a single marked call site for it rather than a settle method of its own; this
/// class is what that call site calls.
/// </summary>
/// <remarks>
/// Safe to call more than once for the same duel — the actual idempotency guard lives one level
/// down, on <c>Player.TryRecordSettledMatch</c>'s settled-match marker, not here. That is what lets
/// <c>LiveMatchGrain</c> retry a settlement a crash interrupted halfway through without tracking
/// exactly which of its own effects committed: a repeated call for a participant already on record
/// applies nothing, and <see cref="ToArchived"/>'s row is a whole-row upsert, safe to repeat outright.
/// </remarks>
public sealed class LiveMatchSettlement(
    IGrainFactory grainFactory,
    IQuestionRepository questions,
    IMatchArchive archive)
{
    /// <summary>Mirrors a live duel into the archive so it can be listed and found by code — call this
    /// on start and, via <see cref="SettleAsync"/>, again on end, exactly as <c>MatchGrain.IndexAsync</c> does.</summary>
    public Task IndexAsync(LiveMatch m, Language lang) => archive.SaveAsync(ToArchived(m, lang));

    /// <summary>
    /// Settles every participant not already named in <paramref name="alreadySettled"/> — empty (or
    /// omitted) the first time a duel ends, and the caller's own persisted progress on a resume after
    /// a crash. Skipping a name here is purely an optimisation, not a correctness requirement: calling
    /// through for a participant already settled is a harmless no-op, so a caller with no progress to
    /// report (every existing call site but <c>LiveMatchGrain</c>'s own resume path) can simply pass
    /// nothing. <paramref name="onSettled"/>, when given, runs right after each participant's effect
    /// lands — <c>LiveMatchGrain</c> uses it to checkpoint <c>SettledPlayers</c> one player at a time
    /// rather than only once at the end.
    /// </summary>
    public async Task SettleAsync(LiveMatch m, Language lang, IReadOnlySet<string>? alreadySettled = null, Func<string, Task>? onSettled = null)
    {
        await IndexAsync(m, lang);

        // No stats, no leaderboard, no result: an expired lobby and a duel lost to a server restart
        // must both leave PlayerStats exactly as they found it.
        if (m.State == MatchState.NoContest) return;

        var byId = (await questions.GetManyAsync(m.QuestionIds)).ToDictionary(q => q.Id);
        var categories = m.Rounds.Select(r => byId.GetValueOrDefault(r.QuestionId)?.CategoryId ?? "unknown").ToList();

        foreach (var playerId in Participants(m))
        {
            if (alreadySettled?.Contains(playerId) == true) continue;

            var correct = m.Rounds.Select(r => r.Answers.TryGetValue(playerId, out var a) && a.Correct).ToList();
            var isQuitter = m.State == MatchState.Abandoned && m.AbandonedBy == playerId;
            var outcome = OutcomeFor(m, playerId, isQuitter);

            // The quitter forfeits everything banked in this duel; a bonus for the other side is
            // exactly what would make abandonment farmable, so they get their real score and nothing more.
            var score = isQuitter ? 0 : m.Score(playerId);
            var abandonedAt = isQuitter ? m.EndedAt ?? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

            // One grain call for both effects: the result and, for the side that walked away, the
            // abandonment penalty. A single settled-match marker cannot guard two separate grain
            // calls without one of them silently no-opping the other, so they have to land together.
            // The grain itself owns the guest exclusion and the unconditional leaderboard projection —
            // this class no longer touches IPlayerRepository or ILeaderboard directly.
            await grainFactory.GetGrain<IPlayerGrain>(playerId)
                .SettleMatchAsync(m.Id, (int)outcome, score, categories, correct, abandonedAt);

            if (onSettled is not null) await onSettled(playerId);
        }
    }

    private static MatchOutcome OutcomeFor(LiveMatch m, string playerId, bool isQuitter)
    {
        if (m.State == MatchState.Abandoned) return isQuitter ? MatchOutcome.Loss : MatchOutcome.Win;
        return m.IsDraw ? MatchOutcome.Draw : (m.WinnerId == playerId ? MatchOutcome.Win : MatchOutcome.Loss);
    }

    // Must agree with LiveMatchGrain.IndexAsync's own row for the same duel field for field — the two
    // write the same archive document at different points in its life (grain on every phase change
    // while it runs, this class again on settlement) and only the scores are meant to differ, since
    // the grain writes 0/0 until settlement knows the real ones. m.Code is the duel's actual share
    // code; passing m.Id here instead — as this once did — would overwrite that code with the id on
    // every settlement and break code resolution for it from then on.
    private static ArchivedMatch ToArchived(LiveMatch m, Language lang) => new(
        m.Id, m.Code, lang, m.ChallengerId, m.OpponentId, m.WinnerId, m.IsDraw,
        m.Score(m.ChallengerId), m.OpponentId is null ? 0 : m.Score(m.OpponentId),
        m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds], IsLive: true);

    private static IEnumerable<string> Participants(LiveMatch m)
    {
        yield return m.ChallengerId;
        if (m.OpponentId is not null) yield return m.OpponentId;
    }
}
