using Quesshi.Application.Media;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class MediaSampleTests
{
    [Fact]
    public Task Jpeg_sample_is_classified_as_image_jpeg()
    {
        Assert.Equal((true, MediaKind.Image, "image/jpeg"), Inspect("sample.jpg"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Png_sample_is_classified_as_image_png()
    {
        Assert.Equal((true, MediaKind.Image, "image/png"), Inspect("sample.png"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Webp_sample_is_classified_as_image_webp()
    {
        Assert.Equal((true, MediaKind.Image, "image/webp"), Inspect("sample.webp"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Mpeg_audio_sample_is_classified_as_audio_mpeg()
    {
        Assert.Equal((true, MediaKind.Audio, "audio/mpeg"), Inspect("sample.mp3"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Ogg_audio_sample_is_classified_as_audio_ogg()
    {
        Assert.Equal((true, MediaKind.Audio, "audio/ogg"), Inspect("sample.ogg"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Wav_sample_is_classified_as_audio_wav()
    {
        Assert.Equal((true, MediaKind.Audio, "audio/wav"), Inspect("sample.wav"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Mp4_sample_is_classified_as_video_mp4()
    {
        Assert.Equal((true, MediaKind.Video, "video/mp4"), Inspect("sample.mp4"));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Webm_sample_is_classified_as_video_webm()
    {
        Assert.Equal((true, MediaKind.Video, "video/webm"), Inspect("sample.webm"));
        return Task.CompletedTask;
    }

    private static (bool Accepted, MediaKind Kind, string Mime) Inspect(string fileName)
    {
        var sample = MediaSamples.All.Single(sample => sample.FileName == fileName);
        var accepted = MediaSniffer.TryInspect(MediaSamples.ReadBytes(sample), out var kind, out var mime, out _);

        return (accepted, kind, mime);
    }
}
