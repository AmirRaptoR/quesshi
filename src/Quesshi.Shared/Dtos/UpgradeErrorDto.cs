namespace Quesshi.Shared;

/// <summary>
/// <c>POST /api/me/upgrade</c>'s failure body. Most of the possible values are
/// <c>OtpResult.ToString().ToLowerInvariant()</c> — the exact convention <c>POST /api/auth/otp/verify</c>
/// already uses for "wrong", "expired", "toomanyattempts" and "alreadyused" — plus <c>"addresstaken"</c>
/// for the one failure unique to this endpoint, and <c>"not_a_guest"</c> for the guard that refuses the
/// whole route to anyone who is not one. No translation key rides along the way
/// <see cref="AdminAuthErrorDto"/>'s does: there is no server-side business state (a lockout, say)
/// attached to any of these, so the browser is free to decide the wording, the same way it already does
/// for every other plain error code in <c>GameEndpoints</c>/<c>AuthEndpoints</c>. The one value the
/// caller actually branches on is <c>"addresstaken"</c>, which is what tells a guest to sign in instead
/// of retrying.
/// </summary>
public sealed record UpgradeErrorDto(string Error);
