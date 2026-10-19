using System.Buffers.Binary;
using Quesshi.Application.Media;

namespace Quesshi.Application.Tests;

public sealed class MediaSnifferTests
{
    [Fact]
    public void Supported_sample_media_has_a_verified_type_and_available_metadata()
    {
        var png = new byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 0, 2, 0, 0, 0, 3,
        };

        var accepted = MediaSniffer.TryInspect(png, out var kind, out var mime, out var metadata);

        Assert.True(accepted);
        Assert.Equal(MediaKind.Image, kind);
        Assert.Equal("image/png", mime);
        Assert.Equal(2, metadata.GetProperty("width").GetInt32());
        Assert.Equal(3, metadata.GetProperty("height").GetInt32());
    }

    [Fact]
    public void Wave_sample_records_available_duration_metadata()
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

        var accepted = MediaSniffer.TryInspect(wave, out var kind, out var mime, out var metadata);

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
}
