using System.Buffers.Binary;
using System.Text.Json;

namespace Quesshi.Application.Media;

/// <summary>Recognises the small baseline set of safe media formats without trusting a client header.</summary>
public static class MediaSniffer
{
    public static bool TryInspect(ReadOnlySpan<byte> bytes, out MediaKind kind, out string mime, out JsonElement meta)
    {
        kind = default;
        mime = string.Empty;
        var values = new Dictionary<string, object?>();
        if (IsPng(bytes))
        {
            kind = MediaKind.Image;
            mime = "image/png";
            values["width"] = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
            values["height"] = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        }
        else if (IsJpeg(bytes, out var width, out var height))
        {
            kind = MediaKind.Image;
            mime = "image/jpeg";
            if (width is not null) values["width"] = width.Value;
            if (height is not null) values["height"] = height.Value;
        }
        else if (IsWebp(bytes))
        {
            kind = MediaKind.Image;
            mime = "image/webp";
        }
        else if (IsWave(bytes, out var duration))
        {
            kind = MediaKind.Audio;
            mime = "audio/wav";
            if (duration is not null) values["durationSeconds"] = duration.Value;
        }
        else if (IsOgg(bytes))
        {
            kind = MediaKind.Audio;
            mime = "audio/ogg";
        }
        else if (IsMpeg(bytes))
        {
            kind = MediaKind.Audio;
            mime = "audio/mpeg";
        }
        else if (IsMp4(bytes))
        {
            kind = MediaKind.Video;
            mime = "video/mp4";
        }
        else if (IsWebm(bytes))
        {
            kind = MediaKind.Video;
            mime = "video/webm";
        }
        else
        {
            meta = default;
            return false;
        }

        meta = JsonSerializer.SerializeToElement(values);
        return true;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.Length >= 24
        && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        && bytes[12..16].SequenceEqual("IHDR"u8);

    private static bool IsWebp(ReadOnlySpan<byte> bytes) => bytes.Length >= 12
        && bytes[..4].SequenceEqual("RIFF"u8)
        && bytes[8..12].SequenceEqual("WEBP"u8);

    private static bool IsOgg(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8);

    private static bool IsMpeg(ReadOnlySpan<byte> bytes) => (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8))
        || (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0);

    private static bool IsMp4(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8);

    private static bool IsWebm(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 });

    private static bool IsWave(ReadOnlySpan<byte> bytes, out double? duration)
    {
        duration = null;
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8)) return false;
        var position = 12;
        uint? byteRate = null;
        uint? dataLength = null;
        while (position <= bytes.Length - 8)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..(position + 8)]);
            var body = position + 8;
            if (length > bytes.Length - body) return false;
            if (bytes[position..(position + 4)].SequenceEqual("fmt "u8) && length >= 8)
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(body + 4)..(body + 8)]);
            if (bytes[position..(position + 4)].SequenceEqual("data"u8)) dataLength = length;
            position = checked(body + (int)length + ((length & 1) == 0 ? 0 : 1));
        }

        if (byteRate is > 0 && dataLength is not null) duration = Math.Round((double)dataLength.Value / byteRate.Value, 3);
        return true;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes, out int? width, out int? height)
    {
        width = null;
        height = null;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var position = 2;
        while (position < bytes.Length - 9)
        {
            if (bytes[position] != 0xff) return false;
            while (position < bytes.Length && bytes[position] == 0xff) position++;
            if (position >= bytes.Length) return false;
            var marker = bytes[position++];
            if (marker is 0xd9 or 0xda) return true;
            if (marker is >= 0xd0 and <= 0xd7) continue;
            if (position + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..(position + 2)]);
            if (length < 2 || position + length > bytes.Length) return false;
            if (marker is >= 0xc0 and <= 0xc3 && length >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..(position + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..(position + 7)]);
                return true;
            }
            position += length;
        }
        return true;
    }
}
