using System.Globalization;

namespace Quesshi.Domain;

/// <summary>
/// Coordinates on the way in and out of a map question: the one place that formats a latitude and
/// longitude into the answer string, the one place that reads one back, and the distance function
/// that decides whether a dropped pin counts.
/// </summary>
public static class Geo
{
    /// <summary>Mean Earth radius. Haversine on a sphere is off by up to ~0.5% against the real
    /// ellipsoid, which at the smallest radius we allow (10 km) is fifty metres — far below the
    /// precision of a fingertip on a world map, so the extra arithmetic of Vincenty buys nothing.</summary>
    public const double EarthRadiusKm = 6371.0088;

    public const double MinLatitude = -90;
    public const double MaxLatitude = 90;
    public const double MinLongitude = -180;
    public const double MaxLongitude = 180;

    /// <summary>
    /// True for a latitude that is both in range and an actual number. The second half is the point:
    /// NaN fails every comparison it is given, so <c>lat is < -90 or > 90</c> waves it through and
    /// every distance computed from it is then NaN, which compares false against any radius — a
    /// question nobody can ever answer correctly, with nothing anywhere reporting a fault.
    /// </summary>
    public static bool IsValidLatitude(double latitude)
        => double.IsFinite(latitude) && latitude >= MinLatitude && latitude <= MaxLatitude;

    /// <inheritdoc cref="IsValidLatitude"/>
    public static bool IsValidLongitude(double longitude)
        => double.IsFinite(longitude) && longitude >= MinLongitude && longitude <= MaxLongitude;

    /// <summary>
    /// Great-circle distance in kilometres. Haversine rather than the spherical law of cosines
    /// because the latter loses its precision at exactly the distances a tight city radius cares
    /// about.
    /// </summary>
    public static double DistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = double.DegreesToRadians(latitude1);
        var lat2 = double.DegreesToRadians(latitude2);
        var dLat = double.DegreesToRadians(latitude2 - latitude1);
        var dLon = double.DegreesToRadians(longitude2 - longitude1);

        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>
    /// The wire form of a point: <c>"52.37,4.9"</c>. Always
    /// <see cref="CultureInfo.InvariantCulture"/>, never the ambient one. This app runs in Persian
    /// and the Blazor client takes the browser's culture, so a bare <c>ToString()</c> on a Persian
    /// thread emits Persian digits and an Arabic decimal separator; the server then fails to parse
    /// its own format and the failure reads as "the map never accepts my answer", in one language
    /// only. Six decimals is about a tenth of a metre — far more than a tap on a world map can
    /// mean, and short enough to stay readable in a log.
    /// </summary>
    public static string Format(double latitude, double longitude)
        => string.Concat(FormatCoordinate(latitude), ",", FormatCoordinate(longitude));

    /// <inheritdoc cref="Format(double, double)"/>
    public static string FormatCoordinate(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads back what <see cref="Format(double, double)"/> wrote, and nothing else. Returns false
    /// rather than throwing: this parses a string that arrived over the wire from a client we do
    /// not control.
    /// <para>
    /// <see cref="NumberStyles.Float"/> without <see cref="NumberStyles.AllowThousands"/> is
    /// deliberate. It makes <c>"52,37"</c> — a Persian or German decimal comma — a parse failure
    /// instead of the thousands-separated 5237 it would otherwise become, and it rejects Persian
    /// digits outright. Misparsing beats failing to parse only if you never have to explain to a
    /// player why their answer landed in the Indian Ocean.
    /// </para>
    /// <para>
    /// Non-finite values are rejected explicitly, because .NET Core happily parses the literal
    /// strings "NaN" and "Infinity" under the invariant culture.
    /// </para>
    /// </summary>
    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(',');
        if (parts.Length != 2) return false;

        if (!TryParseCoordinate(parts[0], out var lat) || !TryParseCoordinate(parts[1], out var lon))
            return false;
        if (!IsValidLatitude(lat) || !IsValidLongitude(lon)) return false;

        latitude = lat;
        longitude = lon;
        return true;
    }

    private static bool TryParseCoordinate(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
