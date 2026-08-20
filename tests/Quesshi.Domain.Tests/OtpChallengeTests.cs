using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class OtpChallengeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static OtpChallenge New(string code = "123456") => OtpChallenge.Issue("a@b.com", code, T0);

    [Fact]
    public void The_right_code_verifies()
        => Assert.Equal(OtpResult.Ok, New().Verify("123456", T0));

    [Fact]
    public void A_wrong_code_is_rejected()
        => Assert.Equal(OtpResult.Wrong, New().Verify("000000", T0));

    [Fact]
    public void A_code_expires()
        => Assert.Equal(OtpResult.Expired, New().Verify("123456", T0 + OtpChallenge.Lifetime + TimeSpan.FromSeconds(1)));

    [Fact]
    public void A_code_works_only_once()
    {
        var otp = New();
        Assert.Equal(OtpResult.Ok, otp.Verify("123456", T0));
        Assert.Equal(OtpResult.AlreadyUsed, otp.Verify("123456", T0));
    }

    [Fact]
    public void Guessing_is_cut_off_after_the_attempt_limit()
    {
        var otp = New();
        for (var i = 0; i < OtpChallenge.MaxAttempts; i++)
            Assert.Equal(OtpResult.Wrong, otp.Verify("999999", T0));

        Assert.Equal(OtpResult.TooManyAttempts, otp.Verify("999999", T0));
        Assert.Equal(OtpResult.TooManyAttempts, otp.Verify("123456", T0));
    }

    [Fact]
    public void Generated_codes_are_six_digits()
    {
        var code = OtpChallenge.NewCode();
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
    }
}
