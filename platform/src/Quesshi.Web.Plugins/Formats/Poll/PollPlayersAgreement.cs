namespace Quesshi.Web.Plugins.Formats.Poll;

/// <summary>
/// Whether the participants who answered a roster question picked the same participant. A miss
/// carries <c>-1</c> and is excluded rather than counted as a disagreement.
/// </summary>
public static class PlayersAgreement
{
    public static PlayersAgreementState State(IEnumerable<int> choiceIndices)
    {
        var answered = choiceIndices.Where(c => c >= 0).ToList();
        if (answered.Count < 2) return PlayersAgreementState.NotEnoughAnswers;
        return answered.Distinct().Count() == 1 ? PlayersAgreementState.Agreed : PlayersAgreementState.Disagreed;
    }

    /// <summary><c>false</c>, not "unknown", when fewer than two answered: there is no agreement to report yet.</summary>
    public static bool AllAgreed(IEnumerable<int> choiceIndices) => State(choiceIndices) == PlayersAgreementState.Agreed;
}

public enum PlayersAgreementState { NotEnoughAnswers, Agreed, Disagreed }
