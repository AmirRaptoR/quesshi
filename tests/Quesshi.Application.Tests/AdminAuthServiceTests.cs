using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public class AdminAuthServiceTests
{
    private const string Password = "correct horse battery";

    private readonly InMemoryAdminUsers _admins = new();
    private readonly InMemoryResetTokens _tokens = new();
    private readonly CapturingAdminMailer _mailer = new();
    private readonly FakeHasher _hasher = new();
    private readonly FakeClock _clock = FakeClock.At2026();
    private readonly SeqIds _ids = new();

    private AdminAuthService Sut() => new(_admins, _tokens, _hasher, _mailer, _clock, _ids);

    private AdminUser Existing(bool active = true, bool mustChange = false)
    {
        var user = AdminUser.Create("a1", "amir", "amir@example.com", _hasher.Hash(Password), _clock.Now, mustChange);
        if (!active) user.Deactivate();
        _admins.UpsertAsync(user);
        return user;
    }

    // --- signing in ------------------------------------------------------------------

    [Fact]
    public async Task The_right_password_signs_in()
    {
        Existing();
        var result = await Sut().LoginAsync("amir", Password);

        Assert.Equal(AdminLoginStatus.Ok, result.Status);
        Assert.NotNull(result.User);
        Assert.Equal(_clock.Now, result.User!.LastLoginAt);
    }

    [Fact]
    public async Task The_username_is_not_case_sensitive()
        => Assert.Equal(AdminLoginStatus.Ok, (await SignInAs("  AMIR ")).Status);

    private async Task<AdminLoginResult> SignInAs(string username)
    {
        Existing();
        return await Sut().LoginAsync(username, Password);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_counted()
    {
        Existing();
        var result = await Sut().LoginAsync("amir", "not the password");

        Assert.Equal(AdminLoginStatus.InvalidCredentials, result.Status);
        Assert.Null(result.User);
        Assert.Equal(1, (await _admins.GetByUsernameAsync("amir"))!.FailedAttempts);
    }

    [Fact]
    public async Task An_unknown_username_looks_exactly_like_a_wrong_password()
    {
        Existing();
        var result = await Sut().LoginAsync("nobody", Password);

        Assert.Equal(AdminLoginStatus.InvalidCredentials, result.Status);

        // and it costs the same work, so the response time does not reveal who exists
        Assert.Equal(1, _hasher.BurnCount);
    }

    [Fact]
    public async Task A_deactivated_admin_is_refused_without_saying_so()
    {
        Existing(active: false);
        Assert.Equal(AdminLoginStatus.InvalidCredentials, (await Sut().LoginAsync("amir", Password)).Status);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_the_right_password_then_fails_too()
    {
        Existing();
        var sut = Sut();

        for (var i = 0; i < AdminUser.MaxFailedAttempts; i++)
            await sut.LoginAsync("amir", "wrong");

        var result = await sut.LoginAsync("amir", Password);

        Assert.Equal(AdminLoginStatus.Locked, result.Status);
        Assert.NotNull(result.LockedUntil);
    }

    [Fact]
    public async Task The_lock_lifts_by_itself()
    {
        Existing();
        var sut = Sut();
        for (var i = 0; i < AdminUser.MaxFailedAttempts; i++) await sut.LoginAsync("amir", "wrong");

        _clock.Now += AdminUser.LockDuration + TimeSpan.FromMinutes(1);

        Assert.Equal(AdminLoginStatus.Ok, (await sut.LoginAsync("amir", Password)).Status);
    }

    [Fact]
    public async Task A_temporary_password_signs_in_but_demands_a_change()
    {
        Existing(mustChange: true);
        var result = await Sut().LoginAsync("amir", Password);

        Assert.Equal(AdminLoginStatus.MustChangePassword, result.Status);
        Assert.NotNull(result.User);
    }

    // --- forgotten password ----------------------------------------------------------

    [Fact]
    public async Task Asking_to_reset_an_unknown_account_says_nothing_and_sends_nothing()
    {
        Existing();
        await Sut().RequestResetAsync("nobody", "https://quesshi.test/admin/reset");

        Assert.Equal(0, _mailer.Sends);
    }

    [Fact]
    public async Task A_reset_link_can_be_requested_by_username_or_by_email()
    {
        Existing();
        var sut = Sut();

        await sut.RequestResetAsync("amir", "https://quesshi.test/admin/reset");
        await sut.RequestResetAsync("amir@example.com", "https://quesshi.test/admin/reset");

        Assert.Equal(2, _mailer.Sends);
        Assert.Equal("amir@example.com", _mailer.LastEmail);
        Assert.Contains("token=", _mailer.LastLink);
    }

    [Fact]
    public async Task A_reset_link_sets_a_new_password_and_unlocks_the_account()
    {
        var user = Existing();
        for (var i = 0; i < AdminUser.MaxFailedAttempts; i++) user.RecordFailure(_clock.Now);
        await _admins.UpsertAsync(user);

        var sut = Sut();
        await sut.RequestResetAsync("amir", "https://quesshi.test/admin/reset");
        var secret = SecretFrom(_mailer.LastLink!);

        Assert.Equal(PasswordChangeStatus.Ok, (await sut.ResetAsync(secret, "a whole new password")).Status);
        Assert.Equal(AdminLoginStatus.Ok, (await sut.LoginAsync("amir", "a whole new password")).Status);
    }

    [Fact]
    public async Task A_reset_link_works_only_once()
    {
        Existing();
        var sut = Sut();
        await sut.RequestResetAsync("amir", "https://quesshi.test/admin/reset");
        var secret = SecretFrom(_mailer.LastLink!);

        await sut.ResetAsync(secret, "a whole new password");

        Assert.Equal(PasswordChangeStatus.UsedToken, (await sut.ResetAsync(secret, "another new password")).Status);
    }

    [Fact]
    public async Task An_expired_reset_link_is_refused()
    {
        Existing();
        var sut = Sut();
        await sut.RequestResetAsync("amir", "https://quesshi.test/admin/reset");
        var secret = SecretFrom(_mailer.LastLink!);

        _clock.Now += PasswordResetToken.Lifetime + TimeSpan.FromMinutes(1);

        Assert.Equal(PasswordChangeStatus.ExpiredToken, (await sut.ResetAsync(secret, "a whole new password")).Status);
    }

    [Fact]
    public async Task A_made_up_reset_token_is_refused()
        => Assert.Equal(PasswordChangeStatus.InvalidToken, (await Sut().ResetAsync("not-a-real-token", "a whole new password")).Status);

    [Fact]
    public async Task A_reset_will_not_accept_a_weak_password()
    {
        Existing();
        var sut = Sut();
        await sut.RequestResetAsync("amir", "https://quesshi.test/admin/reset");

        var result = await sut.ResetAsync(SecretFrom(_mailer.LastLink!), "short");

        Assert.Equal(PasswordChangeStatus.WeakPassword, result.Status);
        Assert.NotEmpty(result.Problems);
    }

    // --- changing password while signed in -------------------------------------------

    [Fact]
    public async Task Changing_a_password_needs_the_current_one()
    {
        Existing();
        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword,
            (await Sut().ChangePasswordAsync("a1", "wrong", "a whole new password")).Status);
    }

    [Fact]
    public async Task Changing_a_password_refuses_a_weak_replacement()
    {
        Existing();
        Assert.Equal(PasswordChangeStatus.WeakPassword,
            (await Sut().ChangePasswordAsync("a1", Password, "password123")).Status);
    }

    [Fact]
    public async Task A_changed_password_takes_effect_and_clears_the_must_change_flag()
    {
        Existing(mustChange: true);
        var sut = Sut();

        Assert.Equal(PasswordChangeStatus.Ok, (await sut.ChangePasswordAsync("a1", Password, "a whole new password")).Status);
        Assert.Equal(AdminLoginStatus.Ok, (await sut.LoginAsync("amir", "a whole new password")).Status);
    }

    private static string SecretFrom(string link) => link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..];
}
