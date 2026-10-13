using System.Globalization;

namespace Quesshi.Shared;

/// <summary>Culture-invariant coordinate formatting, validation, parsing and great-circle distance.</summary>
public static class Geo
{
    public const double EarthRadiusKm = 6371.0088;
    public const double MinLatitude = -90;
    public const double MaxLatitude = 90;
    public const double MinLongitude = -180;
    public const double MaxLongitude = 180;

    public static bool IsValidLatitude(double latitude) =>
        double.IsFinite(latitude) && latitude >= MinLatitude && latitude <= MaxLatitude;

    public static bool IsValidLongitude(double longitude) =>
        double.IsFinite(longitude) && longitude >= MinLongitude && longitude <= MaxLongitude;

    public static double DistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = double.DegreesToRadians(latitude1);
        var lat2 = double.DegreesToRadians(latitude2);
        var deltaLatitude = double.DegreesToRadians(latitude2 - latitude1);
        var deltaLongitude = double.DegreesToRadians(longitude2 - longitude1);
        var haversine = Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(haversine)));
    }

    public static string Format(double latitude, double longitude) =>
        string.Concat(FormatCoordinate(latitude), ",", FormatCoordinate(longitude));

    public static string FormatCoordinate(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length != 2
            || !TryParseCoordinate(parts[0], out var parsedLatitude)
            || !TryParseCoordinate(parts[1], out var parsedLongitude)
            || !IsValidLatitude(parsedLatitude)
            || !IsValidLongitude(parsedLongitude)) return false;

        latitude = parsedLatitude;
        longitude = parsedLongitude;
        return true;
    }

    private static bool TryParseCoordinate(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
