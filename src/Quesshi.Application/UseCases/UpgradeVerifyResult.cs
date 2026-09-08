using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>
/// <see cref="AuthService.VerifyUpgradeAsync"/>'s outcome. <paramref name="Email"/> is the normalized
/// address, present only when <paramref name="Result"/> is <see cref="OtpResult.Ok"/> — the one case
/// in which the caller (<c>GameEndpoints</c>) has something to hand <c>IPlayerGrain.ClaimEmailAsync</c>.
/// Deliberately not a <c>Player</c>: this service checks the address is unused, it does not claim it —
/// claiming is the grain's job, per the "every mutation of a Player goes through IPlayerGrain" rule.
/// </summary>
public sealed record UpgradeVerifyResult(OtpResult Result, string? Email);
