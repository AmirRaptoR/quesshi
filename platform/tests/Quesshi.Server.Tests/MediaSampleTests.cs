using Quesshi.Application.Media;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class MediaSampleTests
{
    [Fact]
    public void Jpeg_sample_is_classified_as_image_jpeg()
    {
        Assert.Equal((true, MediaKind.Image, "image/jpeg"), Inspect("sample.jpg"));
    }

    [Fact]
    public void Png_sample_is_classified_as_image_png()
    {
        Assert.Equal((true, MediaKind.Image, "image/png"), Inspect("sample.png"));
    }

    [Fact]
    public void Webp_sample_is_classified_as_image_webp()
    {
        Assert.Equal((true, MediaKind.Image, "image/webp"), Inspect("sample.webp"));
    }

    [Fact]
    public void Mpeg_audio_sample_is_classified_as_audio_mpeg()
    {
        Assert.Equal((true, MediaKind.Audio, "audio/mpeg"), Inspect("sample.mp3"));
    }

    [Fact]
    public void Ogg_audio_sample_is_classified_as_audio_ogg()
    {
        Assert.Equal((true, MediaKind.Audio, "audio/ogg"), Inspect("sample.ogg"));
    }

    [Fact]
    public void Wav_sample_is_classified_as_audio_wav()
    {
        Assert.Equal((true, MediaKind.Audio, "audio/wav"), Inspect("sample.wav"));
    }

    [Fact]
    public void Mp4_sample_is_classified_as_video_mp4()
    {
        Assert.Equal((true, MediaKind.Video, "video/mp4"), Inspect("sample.mp4"));
    }

    [Fact]
    public void Webm_sample_is_classified_as_video_webm()
    {
        Assert.Equal((true, MediaKind.Video, "video/webm"), Inspect("sample.webm"));
    }

    private static (bool Accepted, MediaKind Kind, string Mime) Inspect(string fileName)
    {
        var sample = MediaSamples.All.Single(sample => sample.FileName == fileName);
        var accepted = MediaSniffer.TryInspect(MediaSamples.ReadBytes(sample), out var kind, out var mime, out _);

        return (accepted, kind, mime);
    }
}
