namespace Quesshi.Web.Services;

/// <summary>
/// A <see cref="Quesshi.Domain.QuestionKind.Players"/> round has no correct answer to reveal — the
/// one thing worth showing is whether everyone who answered picked the same participant. Pulled out
/// of the markup for the same reason <see cref="AnswerReveal"/> and <see cref="LiveOutcomeText"/>
/// were: this project has no bUnit, so a mapping that lives in a <c>.razor</c> file is a mapping with
/// no test.
/// </summary>
public static class PlayersAgreement
{
    /// <summary>
    /// Whether every participant who actually answered picked the same option. A miss carries
    /// <c>-1</c> (the same sentinel every other kind's timeout uses) and is excluded rather than
    /// counted as a disagreement — a round nobody but one player answered has nothing to agree or
    /// disagree about. <c>false</c>, not "unknown", when fewer than two answered: there is no
    /// agreement to report yet, and a screen has to draw one message or the other.
    /// </summary>
    public static bool AllAgreed(IEnumerable<int> choiceIndices)
    {
        var answered = choiceIndices.Where(c => c >= 0).ToList();
        return answered.Count >= 2 && answered.Distinct().Count() == 1;
    }
}
