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
}
