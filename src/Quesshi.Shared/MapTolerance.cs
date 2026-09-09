namespace Quesshi.Shared;

/// <summary>
/// The bounds on a city question's tolerance radius, as the browser knows them.
///
/// <para>
/// They are a copy of <c>Quesshi.Domain.MapTarget.MinRadiusKm</c> and <c>MaxRadiusKm</c>, which is
/// where the rule actually lives and is enforced. The copy exists because <c>Quesshi.Web</c>
/// references only this project — that is what keeps the wire contract free of the domain — and the
/// admin form needs the two numbers to set a slider's ends. The alternative, letting a browser
/// reach into the domain, would cost far more than two constants, and the alternative to
/// <i>that</i> — hard-coding 10 and 2000 in a Razor file — is the same duplication with nothing
/// naming it. <c>WorldMapProjection</c> carries the Earth's radius for exactly the same reason.
/// </para>
/// <para>
/// A test pins these to the domain's own values (<c>MapCoordinateFormatTests</c>), so a bound
/// tightened on one side cannot quietly leave the other side offering a radius the server refuses.
/// </para>
/// </summary>
public static class MapTolerance
{
    /// <summary>The tightest radius worth setting, in kilometres.</summary>
    public const double MinKm = 10;

    /// <summary>The loosest.</summary>
    public const double MaxKm = 2000;

    /// <summary>Where a new city question starts: the middle of what the spec calls normal, since a
    /// form that opened at either bound would be quietly recommending it.</summary>
    public const double DefaultKm = 150;
}
