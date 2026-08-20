using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// Finds a freely-licensed picture of a subject and makes it locally available.
///
/// The image is fetched rather than generated: a generated picture of "the flag of Germany" can be
/// subtly wrong, and a quiz cannot carry that. A real photograph of a real thing can.
/// </summary>
public interface IQuestionImageProvider
{
    bool IsConfigured { get; }

    /// <summary>
    /// Returns a stored, attributed image for the subject, or null when there is no usable one.
    /// Must not throw: no picture means no media question, never a failed batch.
    /// </summary>
    Task<MediaRef?> ProvideAsync(string subject, CancellationToken ct = default);
}
