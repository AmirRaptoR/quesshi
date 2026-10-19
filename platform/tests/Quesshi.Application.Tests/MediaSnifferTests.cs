using System.Buffers.Binary;
using Quesshi.Application.Media;

namespace Quesshi.Application.Tests;

public sealed class MediaSnifferTests
{
    public static IEnumerable<object[]> SupportedSamples =>
    [
        [Jpeg(), MediaKind.Image, "image/jpeg"],
        [Png(), MediaKind.Image, "image/png"],
        [Webp(), MediaKind.Image, "image/webp"],
        ["ID3"u8.ToArray(), MediaKind.Audio, "audio/mpeg"],
        ["OggS"u8.ToArray(), MediaKind.Audio, "audio/ogg"],
        [Wave(), MediaKind.Audio, "audio/wav"],
        [Mp4(), MediaKind.Video, "video/mp4"],
        [Webm(), MediaKind.Video, "video/webm"],
    ];

    [Theory]
    [MemberData(nameof(SupportedSamples))]
    public void Each_supported_baseline_format_has_its_verified_kind_and_mime(byte[] bytes, MediaKind expectedKind, string expectedMime)
    {
        var accepted = MediaSniffer.TryInspect(bytes, out var kind, out var mime, out _);

        Assert.True(accepted);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedMime, mime);
    }

    [Fact]
    public void Jpeg_sample_records_available_width_and_height_metadata()
    {
        var accepted = MediaSniffer.TryInspect(Jpeg(), out _, out _, out var metadata);

        Assert.True(accepted);
        Assert.Equal(2, metadata.GetProperty("width").GetInt32());
        Assert.Equal(3, metadata.GetProperty("height").GetInt32());
    }

    [Fact]
    public void Wave_sample_records_available_duration_metadata()
    {
        var accepted = MediaSniffer.TryInspect(Wave(), out var kind, out var mime, out var metadata);

        Assert.True(accepted);
        Assert.Equal(MediaKind.Audio, kind);
        Assert.Equal("audio/wav", mime);
        Assert.Equal(2, metadata.GetProperty("durationSeconds").GetDouble());
    }

    [Theory]
    [InlineData("not media")]
    [InlineData("")]
    public void Unsupported_bytes_are_not_classified_as_media(string value)
    {
        Assert.False(MediaSniffer.TryInspect(System.Text.Encoding.UTF8.GetBytes(value), out _, out _, out _));
    }

    [Fact]
    public void Webm_bytes_are_not_classified_as_mp4()
    {
        var accepted = MediaSniffer.TryInspect(Webm(), out _, out var mime, out _);

        Assert.True(accepted);
        Assert.NotEqual("video/mp4", mime);
    }

    private static byte[] Jpeg() => [0xff, 0xd8, 0xff, 0xc0, 0x00, 0x07, 0x08, 0x00, 0x03, 0x00, 0x02, 0xff, 0xd9];
    private static byte[] Png() => [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 2, 0, 0, 0, 3];
    private static byte[] Webp() => [82, 73, 70, 70, 0, 0, 0, 0, 87, 69, 66, 80];
    private static byte[] Mp4() => [0, 0, 0, 0, 102, 116, 121, 112, 0, 0, 0, 0];
    private static byte[] Webm() => [0x1a, 0x45, 0xdf, 0xa3];

    private static byte[] Wave()
    {
        var wave = new byte[2_044];
        "RIFF"u8.CopyTo(wave);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 1_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 1_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 8);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), 2_000);
        return wave;
    }
}
