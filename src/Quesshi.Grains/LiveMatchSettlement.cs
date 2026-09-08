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

        // A no-contest credits nobody: no stats, no leaderboard, no result. An expired lobby and a
        // duel lost to a server restart must both leave PlayerStats exactly as they found it — which
        // is why the reason matters and the state alone is not enough to decide. Everybody walking
        // away is also a no-contest, and letting that one off would make mass abandonment the
        // cheapest way to dodge a penalty that a single abandoner pays in full: the winning move in a
        // three-player duel would be to agree to all quit. So AllAbandoned still settles, for the
        // penalty alone.
        if (m.State == MatchState.NoContest)
        {
            if (m.Reason == NoContestReason.AllAbandoned)
                await SettlePenaltiesOnlyAsync(m, alreadySettled, onSettled);
            return;
        }

        var byId = (await questions.GetManyAsync(m.QuestionIds)).ToDictionary(q => q.Id);
        var categories = m.Rounds.Select(r => byId.GetValueOrDefault(r.QuestionId)?.CategoryId ?? "unknown").ToList();

        // Every seated player, not just the first two: this used to walk a private Participants(m)
        // iterator that yielded only ChallengerId and OpponentId, so a capacity>2 duel silently
        // skipped every player past the second here — no stats, no leaderboard, no abandonment
        // penalty for them, ever, and nothing about it failed loudly since the obsolete accessors it
        // read from still compile. LiveMatch.Participants is the one list that actually has everyone.
        foreach (var playerId in m.Participants)
        {
            if (alreadySettled?.Contains(playerId) == true) continue;

            var correct = m.Rounds.Select(r => r.Answers.TryGetValue(playerId, out var a) && a.Correct).ToList();

            // Every abandoner is a quitter here, not just the first: an Abandoned duel's non-survivors
            // are all abandoners by definition (LiveMatch.CloseRound only ever reaches Abandoned once
            // exactly one player is left active), so a capacity>2 duel can have several. This used to
            // read the deleted LiveMatch.AbandonedBy accessor, which named only Abandoners[0] — every
            // quitter past the first was settled as an ordinary loser instead: real score kept, no
            // abandonment penalty, ever, for anyone but whoever happened to drop first.
            var isQuitter = m.State == MatchState.Abandoned && m.Abandoners.Any(a => a.PlayerId == playerId);
            var outcome = m.Standings.First(s => s.PlayerId == playerId).Outcome;

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

    // Must agree with LiveMatchGrain.IndexAsync's own row for the same duel field for field — the two
    // write the same archive document at different points in its life (grain on every phase change
    // while it runs, this class again on settlement) and only the scores are meant to differ, since
    // the grain writes 0/0 until settlement knows the real ones. m.Code is the duel's actual share
    // code; passing m.Id here instead — as this once did — would overwrite that code with the id on
    // every settlement and break code resolution for it from then on.
    // The two arguments below are ArchivedMatch's own permanent two-scalar fields, kept for the rows
    // and readers that still switch on them (MatchDoc's legacy shape, Mappers.ToLiveSummary's history
    // of the same); they are not where this row's participant set lives any more, and — now that
    // LiveMatch no longer offers ChallengerId/OpponentId as a shortcut for them — read straight off
    // Participants instead. BuildResults below is the one that actually carries every seat: it walks
    // m.Participants, not these two, so every seat — third and later included — gets a real
    // ParticipantResult regardless of what these two say.
    private static ArchivedMatch ToArchived(LiveMatch m, Language lang)
    {
        var opponentId = m.Participants.Count > 1 ? m.Participants[1] : null;
        return new(m.Id, m.Code, lang, m.Participants[0], opponentId, m.WinnerId, m.IsDraw,
            BuildResults(m), m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds], IsLive: true);
    }

    /// <summary>
    /// Score here is always the raw round total from <see cref="LiveMatch.Score"/>, never zeroed for a
    /// quitter the way <c>Standing.Score</c> is: this is the archived history row, "what actually
    /// happened in the game", not the stats effect — <see cref="SettleAsync"/>'s own <c>score</c> local
    /// above already applies the zero-for-abandoner rule separately, for <c>PlayerStats</c> and the
    /// leaderboard. Place and outcome do come from <see cref="LiveMatch.Standings"/>, the only place
    /// that knows them for N players; a duel that ended <see cref="MatchState.NoContest"/> has no
    /// standings at all (nobody is credited), so every participant reads as unranked there — exactly
    /// what <c>ChallengerScore</c>/<c>OpponentScore</c> always showed for a no-contest before this type
    /// existed, since nothing reads placement once <c>State</c> alone says nobody won.
    /// </summary>
    /// <summary>
    /// The penalty, and nothing else, for a duel everybody walked out of. There is no result to
    /// record — <see cref="MatchOutcome"/> has no value meaning "nothing happened" and a no-contest
    /// credits nobody — so this passes a null outcome and an empty answer history, the shape
    /// <c>SettleMatchAsync</c> takes precisely for this case. The abandonment timestamp is the duel's
    /// own <c>EndedAt</c> rather than the wall clock, so a retry an hour later computes the same
    /// penalty tier and counts from the same moment.
    /// </summary>
    private async Task SettlePenaltiesOnlyAsync(LiveMatch m, IReadOnlySet<string>? alreadySettled, Func<string, Task>? onSettled)
    {
        var at = m.EndedAt ?? DateTimeOffset.UtcNow;

        foreach (var abandonment in m.Abandoners)
        {
            if (alreadySettled?.Contains(abandonment.PlayerId) == true) continue;

            await grainFactory.GetGrain<IPlayerGrain>(abandonment.PlayerId)
                .SettleMatchAsync(m.Id, null, 0, [], [], at);

            if (onSettled is not null) await onSettled(abandonment.PlayerId);
        }
    }

    private static List<ParticipantResult> BuildResults(LiveMatch m)
    {
        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        return [.. m.Participants.Select(pid => byId.TryGetValue(pid, out var s)
            ? new ParticipantResult(pid, m.Score(pid), s.Place, s.Outcome)
            : new ParticipantResult(pid, m.Score(pid), 0, MatchOutcome.Loss))];
    }
}
