namespace Quesshi.Shared;

/// <summary>The fixed avatar palette supported by player profile endpoints and forms.</summary>
public static class PlayerAvatarPalette
{
    public const string Default = "avatar-0";

    private static readonly string[] Values =
    [
        "avatar-0", "avatar-1", "avatar-2", "avatar-3", "avatar-4", "avatar-5",
        "avatar-6", "avatar-7", "avatar-8", "avatar-9", "avatar-10", "avatar-11",
    ];

    public static IReadOnlyList<string> Seeds => Values;

    public static bool IsValid(string? seed) => seed is not null && Array.IndexOf(Values, seed) >= 0;
}
