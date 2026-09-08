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
/// Has no idempotency guard, exactly like <c>MatchGrain.SettleAsync</c> does not: it must be called
/// exactly once per duel, on the transition into <see cref="LiveMatch.IsOver"/>, the same way
/// <c>MatchGrain.AnswerAsync</c> checks <c>!wasOver &amp;&amp; _match.IsOver</c> before calling in. A
/// caller that settles twice will apply every effect twice.
/// </remarks>
public sealed class LiveMatchSettlement(
    IGrainFactory grainFactory,
    IQuestionRepository questions,
    IMatchArchive archive)
{
    /// <summary>Mirrors a live duel into the archive so it can be listed and found by code — call this
    /// on start and, via <see cref="SettleAsync"/>, again on end, exactly as <c>MatchGrain.IndexAsync</c> does.</summary>
    public Task IndexAsync(LiveMatch m, Language lang) => archive.SaveAsync(ToArchived(m, lang));

    public async Task SettleAsync(LiveMatch m, Language lang)
    {
        await IndexAsync(m, lang);

        // No stats, no leaderboard, no result: an expired lobby and a duel lost to a server restart
        // must both leave PlayerStats exactly as they found it.
        if (m.State == MatchState.NoContest) return;

        var byId = (await questions.GetManyAsync(m.QuestionIds)).ToDictionary(q => q.Id);
        var categories = m.Rounds.Select(r => byId.GetValueOrDefault(r.QuestionId)?.CategoryId ?? "unknown").ToList();

        foreach (var playerId in Participants(m))
        {
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
        }
    }

    private static MatchOutcome OutcomeFor(LiveMatch m, string playerId, bool isQuitter)
    {
        if (m.State == MatchState.Abandoned) return isQuitter ? MatchOutcome.Loss : MatchOutcome.Win;
        return m.IsDraw ? MatchOutcome.Draw : (m.WinnerId == playerId ? MatchOutcome.Win : MatchOutcome.Loss);
    }

    private static ArchivedMatch ToArchived(LiveMatch m, Language lang) => new(
        // A live duel has no invite code of its own; its id stands in, exactly as "found by code"
        // means found by this class's own code, per MatchGrain.IndexAsync's comment, not a redeemable one.
        m.Id, m.Id, lang, m.ChallengerId, m.OpponentId, m.WinnerId, m.IsDraw,
        m.Score(m.ChallengerId), m.OpponentId is null ? 0 : m.Score(m.OpponentId),
        m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds], IsLive: true);

    private static IEnumerable<string> Participants(LiveMatch m)
    {
        yield return m.ChallengerId;
        if (m.OpponentId is not null) yield return m.OpponentId;
    }
}
