using System.Globalization;
using System.Text;

namespace Quesshi.Shared;

public static class EmojiIcon
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? "";
        if (normalized.Length == 0) return false;

        var elements = StringInfo.GetTextElementEnumerator(normalized);
        if (!elements.MoveNext() || !string.Equals((string)elements.Current, normalized, StringComparison.Ordinal)
            || elements.MoveNext()) return false;

        var runes = normalized.EnumerateRunes().ToArray();
        if (runes.All(IsRegionalIndicator)) return runes.Length == 2;

        var expectBase = true;
        var sawBase = false;
        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            if (expectBase)
            {
                if (!IsEmojiBase(rune)) return false;
                sawBase = true;
                expectBase = false;
                continue;
            }

            if (rune.Value is 0xFE0F or 0x20E3 || IsModifier(rune)) continue;
            if (rune.Value == 0x200D)
            {
                expectBase = true;
                continue;
            }
            return false;
        }

        return sawBase && !expectBase;
    }

    private static bool IsRegionalIndicator(Rune rune) => rune.Value is >= 0x1F1E6 and <= 0x1F1FF;
    private static bool IsModifier(Rune rune) => rune.Value is >= 0x1F3FB and <= 0x1F3FF;

    private static bool IsEmojiBase(Rune rune) => rune.Value switch
    {
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299 => true,
        >= 0x2194 and <= 0x21FF => true,
        >= 0x2300 and <= 0x23FF => true,
        >= 0x2600 and <= 0x27BF => true,
        >= 0x2B00 and <= 0x2BFF => true,
        >= 0x1F000 and <= 0x1FAFF => !IsRegionalIndicator(rune) && !IsModifier(rune),
        _ => false
    };
}
