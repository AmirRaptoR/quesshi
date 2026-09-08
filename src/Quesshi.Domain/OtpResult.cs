namespace Quesshi.Domain;

/// <summary>
/// <see cref="AddressTaken"/> is never produced by <see cref="OtpChallenge.Verify"/> itself — the
/// challenge only ever knows whether the code was right, wrong, expired, reused, or guessed too many
/// times. It is added to this enum rather than a parallel one because the guest upgrade path
/// (<c>AuthService.VerifyUpgradeAsync</c>) is otherwise identical to ordinary verification and reports
/// through the exact same channel: a business-rule failure discovered immediately after a challenge
/// verifies <see cref="Ok"/>, once the caller can act on that success. It never appears as an
/// <see cref="OtpChallenge"/> outcome and is refused without mutating anything the challenge owns.
/// </summary>
public enum OtpResult { Ok, Wrong, Expired, TooManyAttempts, AlreadyUsed, AddressTaken }
