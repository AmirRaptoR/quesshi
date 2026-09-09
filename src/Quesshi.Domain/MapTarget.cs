namespace Quesshi.Domain;

/// <summary>
/// Where a map question's answer is. One of two shapes, never both: a country carries an ISO 3166-1
/// alpha-2 code, a city carries a point and a tolerance radius. Which shape it is decides how an
/// answer is checked, so it is a small owned record rather than four loose columns on
/// <see cref="Question"/> — loose columns would let a country target carry a stray radius that
/// nothing reads and nothing rejects.
/// <para>
/// Built only through <see cref="Country"/> and <see cref="City"/>, so "exactly one shape is
/// populated" holds by construction rather than by a rule someone has to remember to run.
/// </para>
/// </summary>
public sealed record MapTarget
{
    /// <summary>
    /// The tightest radius worth setting. Below this the target is smaller than a fingertip on a
    /// world map that has no pan and no zoom, so the question stops measuring knowledge and starts
    /// measuring how steady the player's thumb is.
    /// </summary>
    public const double MinRadiusKm = 10;

    /// <summary>
    /// The loosest. Above this a continent's worth of the map is "correct", which is not a question.
    /// </summary>
    public const double MaxRadiusKm = 2000;

    private MapTarget(MapTargetKind shape, string? countryCode, double? latitude, double? longitude, double? radiusKm)
    {
        Shape = shape;
        CountryCode = countryCode;
        Latitude = latitude;
        Longitude = longitude;
        RadiusKm = radiusKm;
    }

    public MapTargetKind Shape { get; }

    /// <summary>Upper-case ISO 3166-1 alpha-2, e.g. <c>"DE"</c>. Null for a city target.</summary>
    public string? CountryCode { get; }

    /// <summary>Degrees north, −90..90. Null for a country target.</summary>
    public double? Latitude { get; }

    /// <summary>Degrees east, −180..180. Null for a country target.</summary>
    public double? Longitude { get; }

    /// <summary>How far from the point still counts, in kilometres. Null for a country target.
    /// This doubles as the question's difficulty lever: 50 km is hard, 300 km is gentle.</summary>
    public double? RadiusKm { get; }

    public bool IsCountry => Shape == MapTargetKind.Country;
    public bool IsCity => Shape == MapTargetKind.City;

    /// <summary>
    /// A country target. The code is trimmed and upper-cased here so that everything downstream —
    /// the SVG lookup, the answer comparison, the admin form — sees one spelling of it.
    /// <para>
    /// This checks the <i>form</i> of the code only. Whether the country exists in the bundled map
    /// is a question about a data file the domain does not own, and is checked by
    /// <see cref="Question.Validate"/> against the set of codes its caller supplies.
    /// </para>
    /// </summary>
    public static MapTarget Country(string countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            throw new ArgumentException("A country target needs a country code.", nameof(countryCode));

        var code = countryCode.Trim().ToUpperInvariant();
        if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"'{countryCode}' is not an ISO 3166-1 alpha-2 country code.", nameof(countryCode));

        return new MapTarget(MapTargetKind.Country, code, null, null, null);
    }

    /// <summary>
    /// A city target: a point and how close a pin has to land. NaN and infinity are rejected here
    /// and not merely range-checked — see <see cref="Geo.IsValidLatitude"/> for why a naive range
    /// check lets them through and what that costs.
    /// </summary>
    public static MapTarget City(double latitude, double longitude, double radiusKm)
    {
        if (!Geo.IsValidLatitude(latitude))
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude,
                $"Latitude must be a finite number between {Geo.MinLatitude} and {Geo.MaxLatitude}.");
        if (!Geo.IsValidLongitude(longitude))
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude,
                $"Longitude must be a finite number between {Geo.MinLongitude} and {Geo.MaxLongitude}.");
        if (!double.IsFinite(radiusKm) || radiusKm < MinRadiusKm || radiusKm > MaxRadiusKm)
            throw new ArgumentOutOfRangeException(nameof(radiusKm), radiusKm,
                $"The tolerance radius must be between {MinRadiusKm} and {MaxRadiusKm} km.");

        return new MapTarget(MapTargetKind.City, null, latitude, longitude, radiusKm);
    }

    /// <summary>
    /// Rehydrates a stored target without re-validating it, mirroring how <see cref="Question.Restore"/>
    /// differs from <see cref="Question.Create"/>. This matters because <see cref="Country"/> and
    /// <see cref="City"/> re-validate on every call: a country code dropped from the bundled map, or a
    /// radius bound tightened after the fact, would otherwise throw on load and take the whole question
    /// read down with it, rather than just being a stale value someone can edit. Storage is trusted;
    /// use <see cref="Country"/> or <see cref="City"/> for anything a person or a generator is authoring.
    /// </summary>
    public static MapTarget Restore(MapTargetKind shape, string? countryCode, double? latitude, double? longitude, double? radiusKm)
        => new(shape, countryCode, latitude, longitude, radiusKm);

    /// <summary>
    /// Whether a submitted answer hits this target. A country answer is its code, a city answer is
    /// <c>"lat,lon"</c> as <see cref="Geo.Format(double, double)"/> writes it; anything that does
    /// not parse is simply wrong, because the alternative — throwing on the grading path — turns a
    /// malformed answer from one player into a broken round for everyone.
    /// </summary>
    public bool Matches(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;

        if (IsCountry)
            return string.Equals(response.Trim(), CountryCode, StringComparison.OrdinalIgnoreCase);

        if (!Geo.TryParse(response, out var latitude, out var longitude)) return false;

        // Inclusive: a pin exactly on the boundary is inside the circle the player was shown.
        return Geo.DistanceKm(Latitude!.Value, Longitude!.Value, latitude, longitude) <= RadiusKm!.Value;
    }
}
