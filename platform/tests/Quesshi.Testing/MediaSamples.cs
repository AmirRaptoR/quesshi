using Quesshi.Application.Media;

namespace Quesshi.Testing;

/// <summary>Provides the embedded media corpus shared by platform test projects.</summary>
public static class MediaSamples
{
    public static IReadOnlyList<MediaSample> All { get; } =
    [
        new("sample.jpg", MediaKind.Image, "image/jpeg"),
        new("sample.png", MediaKind.Image, "image/png"),
        new("sample.webp", MediaKind.Image, "image/webp"),
        new("sample.mp3", MediaKind.Audio, "audio/mpeg"),
        new("sample.ogg", MediaKind.Audio, "audio/ogg"),
        new("sample.wav", MediaKind.Audio, "audio/wav"),
        new("sample.mp4", MediaKind.Video, "video/mp4"),
        new("sample.webm", MediaKind.Video, "video/webm"),
    ];

    /// <summary>Reads a sample's original bytes from the shared test assembly.</summary>
    public static byte[] ReadBytes(MediaSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var resourceName = $"Quesshi.Testing.MediaSamples.{sample.FileName}";
        using var stream = typeof(MediaSamples).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The media sample resource '{resourceName}' is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}

/// <summary>The expected classification and embedded-resource filename for a media sample.</summary>
public sealed record MediaSample(string FileName, MediaKind Kind, string Mime);
