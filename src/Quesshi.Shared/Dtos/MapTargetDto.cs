namespace Quesshi.Shared;

/// <summary>
/// A map question's answer as it crosses the wire between the admin panel and the server: one shape,
/// never both, mirroring <c>Quesshi.Domain.MapTarget</c>.
/// <para>
/// It is a nested record rather than five loose fields on <see cref="SaveQuestionDto"/> for the
/// reason the domain gives for its own version: loose fields let a country target carry a stray
/// radius that nothing reads and nothing rejects. Here the whole target is present or absent
/// together, which is also exactly the question the validation table asks — a choice question must
/// have no target at all, and "no target" is a null rather than five nulls that have to agree.
/// </para>
/// </summary>
/// <param name="Shape"><c>"country"</c> or <c>"city"</c>.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 for a country target; null for a city.</param>
/// <param name="Latitude">Degrees north for a city target; null for a country.</param>
/// <param name="Longitude">Degrees east for a city target; null for a country.</param>
/// <param name="RadiusKm">How close a pin has to land, for a city target.</param>
public sealed record MapTargetDto(string Shape, string? CountryCode = null,
    double? Latitude = null, double? Longitude = null, double? RadiusKm = null);
