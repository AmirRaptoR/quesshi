using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IQuestionGenerator
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default);

    /// <summary>
    /// Questions built around a picture, each naming the Wikipedia subject to illustrate. Phrased
    /// so the image carries the question — "which animal is this?" — rather than decorating it.
    /// </summary>
    Task<IReadOnlyList<GeneratedQuestion>> GenerateIllustratedAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default);

    /// <summary>
    /// Sorting questions: four items and the criterion that orders them, with the items returned in
    /// their correct order.
    /// <para>
    /// A separate method rather than a <see cref="QuestionKind"/> parameter on
    /// <see cref="GenerateAsync"/> because the two differ in every part that matters — a different
    /// prompt, a different response schema and a different set of things that can be wrong with the
    /// answer. One method with a switch inside it would be three prompts wearing a trench coat.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<GeneratedQuestion>> GenerateSortAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default);

    /// <summary>
    /// Map questions: a country to find, or a city with its coordinates <i>and</i> the country it is
    /// in. That last part is not redundant — see <see cref="GeneratedQuestion.CountryCode"/>.
    /// </summary>
    Task<IReadOnlyList<GeneratedQuestion>> GenerateMapAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default);
}
