using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class AdminUserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static AdminUser New() => AdminUser.Create("a1", "Amir ", "Admin@Example.com", "hash", T0);

    [Fact]
    public void A_username_is_stored_folded_so_case_cannot_create_a_second_account()
    {
        var user = New();
        Assert.Equal("amir", user.Username);
        Assert.Equal("admin@example.com", user.Email);
    }

    [Fact]
    public void A_new_admin_is_active_and_unlocked()
    {
        var user = New();
        Assert.True(user.IsActive);
        Assert.False(user.IsLocked(T0));
        Assert.Equal(0, user.FailedAttempts);
    }

    [Fact]
    public void Failures_accumulate_and_lock_the_account()
    {
        var user = New();
        for (var i = 0; i < AdminUser.MaxFailedAttempts - 1; i++)
        {
            user.RecordFailure(T0);
            Assert.False(user.IsLocked(T0));
        }

        user.RecordFailure(T0);
        Assert.True(user.IsLocked(T0));
    }

    [Fact]
    public void A_lock_expires_on_its_own()
    {
        var user = New();
        for (var i = 0; i < AdminUser.MaxFailedAttempts; i++) user.RecordFailure(T0);

        Assert.True(user.IsLocked(T0 + AdminUser.LockDuration - TimeSpan.FromMinutes(1)));
        Assert.False(user.IsLocked(T0 + AdminUser.LockDuration + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_successful_sign_in_clears_the_failure_count()
    {
        var user = New();
        user.RecordFailure(T0);
        user.RecordFailure(T0);
        user.RecordSuccess(T0);

        Assert.Equal(0, user.FailedAttempts);
        Assert.Equal(T0, user.LastLoginAt);
        Assert.False(user.IsLocked(T0));
    }

    [Fact]
    public void Setting_a_new_password_unlocks_the_account_and_clears_the_must_change_flag()
    {
        var user = AdminUser.Create("a1", "amir", "a@b.com", "hash", T0, mustChangePassword: true);
        for (var i = 0; i < AdminUser.MaxFailedAttempts; i++) user.RecordFailure(T0);

        user.SetPassword("newhash", T0);

        Assert.Equal("newhash", user.PasswordHash);
        Assert.False(user.IsLocked(T0));
        Assert.False(user.MustChangePassword);
        Assert.Equal(0, user.FailedAttempts);
    }

    [Fact]
    public void A_deactivated_admin_cannot_be_treated_as_signed_in()
    {
        var user = New();
        user.Deactivate();
        Assert.False(user.IsActive);
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("a-long-enough-one")]
    public void A_long_password_is_accepted(string password)
        => Assert.Empty(PasswordPolicy.Problems(password));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("123456789")]
    public void A_short_or_blank_password_is_refused(string password)
        => Assert.NotEmpty(PasswordPolicy.Problems(password));

    [Fact]
    public void An_absurdly_long_password_is_refused_so_hashing_cannot_be_used_as_a_denial_of_service()
        => Assert.NotEmpty(PasswordPolicy.Problems(new string('x', PasswordPolicy.MaxLength + 1)));

    [Theory]
    [InlineData("password123")]
    [InlineData("Password1234")]
    [InlineData("quesshi-admin")]
    public void Obvious_passwords_are_refused(string password)
        => Assert.NotEmpty(PasswordPolicy.Problems(password));
}

public class PasswordResetTokenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_token_is_usable()
    {
        var token = PasswordResetToken.Issue("a1", "secret", T0);
        Assert.Equal(ResetResult.Ok, token.Redeem("secret", T0));
    }

    [Fact]
    public void A_token_works_only_once()
    {
        var token = PasswordResetToken.Issue("a1", "secret", T0);
        token.Redeem("secret", T0);
        Assert.Equal(ResetResult.AlreadyUsed, token.Redeem("secret", T0));
    }

    [Fact]
    public void A_token_expires()
        => Assert.Equal(ResetResult.Expired,
            PasswordResetToken.Issue("a1", "secret", T0).Redeem("secret", T0 + PasswordResetToken.Lifetime + TimeSpan.FromSeconds(1)));

    [Fact]
    public void The_wrong_secret_is_refused()
        => Assert.Equal(ResetResult.Wrong, PasswordResetToken.Issue("a1", "secret", T0).Redeem("guess", T0));

    [Fact]
    public void Generated_secrets_are_long_and_unique()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => PasswordResetToken.NewSecret()).ToList();
        Assert.Equal(50, secrets.Distinct().Count());
        Assert.All(secrets, s => Assert.True(s.Length >= 32));
    }
}
