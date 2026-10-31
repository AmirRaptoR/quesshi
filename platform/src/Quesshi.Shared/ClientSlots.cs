namespace Quesshi.Shared;

/// <summary>A named extension point whose contributions all receive the same typed parameters.</summary>
public sealed record ClientSlot<TParameters>(string Name);

/// <summary>The parameter contracts and names of the client slots.</summary>
public static class ClientSlots
{
    public static readonly ClientSlot<HomeTileParameters> HomeTile = new("home.tile");
    public static readonly ClientSlot<NavItemParameters> NavItem = new("nav.item");
    public static readonly ClientSlot<ProfileSectionParameters> ProfileSection = new("profile.section");
    public static readonly ClientSlot<SessionResultParameters> SessionResult = new("session.result");
}

/// <summary>Parameters for a tile on the home page; <paramref name="EnabledFormats"/> are the formats offered for new play.</summary>
public sealed record HomeTileParameters(IReadOnlyList<string> EnabledFormats);

/// <summary>Parameters for a navigation entry; <paramref name="CurrentPath"/> is the shell's current relative path.</summary>
public sealed record NavItemParameters(string CurrentPath);

/// <summary>Parameters for a section of the player's profile.</summary>
public sealed record ProfileSectionParameters(string PlayerId);

/// <summary>Parameters for a contribution to a finished session's result.</summary>
public sealed record SessionResultParameters(SessionView Session);

/// <summary>Parameters for a format's lobby settings component; <paramref name="IsOwner"/> tells it whether the viewer may change them.</summary>
public sealed record FormatSettingsParameters(SessionView Session, bool IsOwner);
