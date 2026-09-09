namespace Quesshi.Shared;

/// <summary>
/// Converts between latitude/longitude and the coordinate space of the bundled world map SVG
/// (<c>wwwroot/maps/world.svg</c>, viewBox <c>"-180 -90 360 180"</c>).
///
/// This is deliberately trivial. The map is drawn in an <b>equirectangular (Plate Carrée)</b>
/// projection specifically so that this conversion can be plain linear arithmetic rather than a
/// real map projection: longitude maps straight onto x, and latitude maps onto y with a sign
/// flip, because latitude increases northward while SVG's y axis increases downward. There is no
/// trigonometry, no ellipsoid model and no external mapping library — which is also why the
/// client (running in the browser, offline-capable as a PWA) and the server can both use this
/// exact same class and never disagree about where a tap or a stored city landed.
///
/// If the map asset ever stopped being equirectangular this class would need a real projection
/// (and every existing city question's tolerance radius would need re-checking), which is
/// precisely why <c>docs/sorting-and-map-questions.md</c> calls out asserting the projection with
/// a test rather than assuming it: most freely available world SVGs are not equirectangular, and
/// swapping one in here would make every city question wrong by hundreds of kilometres while
/// looking completely fine.
/// </summary>
public static class WorldMapProjection
{
    /// <summary>Longitude at the left edge of the viewBox.</summary>
    public const double MinLongitude = -180;

    /// <summary>Longitude at the right edge of the viewBox.</summary>
    public const double MaxLongitude = 180;

    /// <summary>Latitude at the north pole — the <em>top</em> edge of the viewBox.</summary>
    public const double MaxLatitude = 90;

    /// <summary>Latitude at the south pole — the <em>bottom</em> edge of the viewBox.</summary>
    public const double MinLatitude = -90;

    /// <summary>
    /// The SVG's <c>viewBox</c> attribute, as one source of truth shared with
    /// <c>tools/map/generate_world_map.py</c> (which produced the asset with this exact box) so a
    /// Razor component can lay markers over the map without retyping magic numbers that could
    /// silently drift out of step with the file.
    /// </summary>
    public const string ViewBox = "-180 -90 360 180";

    /// <summary>
    /// Projects a latitude/longitude pair onto the map's viewBox coordinates. Longitude passes
    /// straight through as x; latitude is negated for y because the viewBox's y axis grows
    /// downward (SVG convention) while latitude grows upward (geographic convention).
    /// </summary>
    public static (double X, double Y) Project(double lat, double lon) => (lon, -lat);

    /// <summary>The exact inverse of <see cref="Project"/>: viewBox coordinates back to lat/lon.</summary>
    public static (double Lat, double Lon) Unproject(double x, double y) => (-y, x);
}
