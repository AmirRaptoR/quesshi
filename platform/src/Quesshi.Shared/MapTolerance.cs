namespace Quesshi.Shared;

/// <summary>
/// The bounds on a map-point question's tolerance radius, as the browser knows them.
///
/// <para>
/// They are shared by the platform kind and map client so a point the author can create is always
/// one the server can validate. <c>WorldMapProjection</c> carries the Earth's radius for the same
/// reason.
/// </para>
/// <para>
/// Keeping the bounds named here avoids duplicating the map contract in a client form.
/// </para>
/// </summary>
public static class MapTolerance
{
    /// <summary>The tightest radius worth setting, in kilometres.</summary>
    public const double MinKm = 10;

    /// <summary>The loosest.</summary>
    public const double MaxKm = 2000;

    /// <summary>Where a new map point starts: the middle of what the spec calls normal, since a
    /// form that opened at either bound would be quietly recommending it.</summary>
    public const double DefaultKm = 150;
}
