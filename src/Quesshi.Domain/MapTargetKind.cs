namespace Quesshi.Domain;

/// <summary>
/// Which of the two shapes a <see cref="MapTarget"/> holds. The card carries this — not the target
/// itself, which is the answer — because a country question and a city question are different
/// interactions: one wants a tap on a region confirmed by its name, the other wants a point
/// dropped. A client cannot tell them apart from <see cref="QuestionKind.Map"/> alone.
/// </summary>
public enum MapTargetKind
{
    Country = 0,
    City = 1
}
