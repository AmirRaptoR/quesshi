using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public class AuthServiceTests
{
    private readonly InMemoryPlayers _players = new();
    private readonly InMemoryOtpStore _otps = new();
    private readonly CapturingOtpSender _sender = new();
    private readonly FakeClock _clock = FakeClock.At2026();
    private readonly SeqIds _ids = new();

    private AuthService Sut() => new(_players, _otps, _sender, _clock, _ids);

    [Fact]
    public async Task Requesting_a_code_sends_one()
    {
        await Sut().RequestOtpAsync("Amir@Example.com ", Language.Fa);
        Assert.Equal("amir@example.com", _sender.LastEmail);
        Assert.Equal(6, _sender.LastCode!.Length);
    }

    [Fact]
    public async Task A_correct_code_signs_in_and_creates_the_player_on_first_use()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("amir@example.com", Language.Fa);
        var (result, player) = await sut.VerifyOtpAsync("amir@example.com", _sender.LastCode!);

        Assert.Equal(OtpResult.Ok, result);
        Assert.NotNull(player);
        Assert.Equal("amir@example.com", player!.Email);
        Assert.Single(_players.Items);
    }

    [Fact]
    public async Task Signing_in_again_reuses_the_same_player()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("amir@example.com", Language.Fa);
        var first = (await sut.VerifyOtpAsync("amir@example.com", _sender.LastCode!)).Player!;

        await sut.RequestOtpAsync("amir@example.com", Language.Fa);
        var second = (await sut.VerifyOtpAsync("amir@example.com", _sender.LastCode!)).Player!;

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_players.Items);
    }

    [Fact]
    public async Task A_wrong_code_neither_signs_in_nor_creates_anybody()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("amir@example.com", Language.Fa);
        var (result, player) = await sut.VerifyOtpAsync("amir@example.com", "000000");

        Assert.Equal(OtpResult.Wrong, result);
        Assert.Null(player);
        Assert.Empty(_players.Items);
    }

    [Fact]
    public async Task Verifying_without_ever_requesting_is_rejected()
    {
        var (result, player) = await Sut().VerifyOtpAsync("nobody@example.com", "123456");
        Assert.Equal(OtpResult.Expired, result);
        Assert.Null(player);
    }

    [Fact]
    public async Task Attempts_carry_across_calls_so_guessing_is_actually_limited()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("amir@example.com", Language.Fa);
        for (var i = 0; i < OtpChallenge.MaxAttempts; i++)
            await sut.VerifyOtpAsync("amir@example.com", "000000");

        var (result, _) = await sut.VerifyOtpAsync("amir@example.com", _sender.LastCode!);
        Assert.Equal(OtpResult.TooManyAttempts, result);
    }

    [Fact]
    public async Task A_banned_player_cannot_sign_in()
    {
        var banned = Player.Register("p-banned", "bad@example.com", "Bad", Language.En, _clock.Now);
        banned.SetBanned(true);
        await _players.UpsertAsync(banned);

        var sut = Sut();
        await sut.RequestOtpAsync("bad@example.com", Language.En);
        var (result, player) = await sut.VerifyOtpAsync("bad@example.com", _sender.LastCode!);

        Assert.Equal(OtpResult.Wrong, result);
        Assert.Null(player);
    }

    [Fact]
    public async Task Google_sign_in_creates_the_player_once_and_reuses_it_after()
    {
        var sut = Sut();
        var a = await sut.SignInWithGoogleAsync("amir@example.com", "Amir", Language.En);
        var b = await sut.SignInWithGoogleAsync("amir@example.com", "Amir Renamed", Language.En);

        Assert.Equal(a!.Id, b!.Id);
        Assert.Single(_players.Items);
    }

    // --- guest upgrade (issue #55) -----------------------------------------------------------------
    // VerifyUpgradeAsync deliberately never creates or fetches a Player for the caller — that is the
    // whole point of not routing through VerifyOtpAsync's GetOrCreateAsync. These tests only ever
    // assert on the OtpResult/email pair it hands back and on _players staying exactly as it was;
    // the claim itself is IPlayerGrain's job and is proved at the grain in Quesshi.Server.Tests.

    [Fact]
    public async Task A_correct_code_for_an_unused_address_reports_it_ready_to_claim()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("new@example.com", Language.En);

        var (result, email) = await sut.VerifyUpgradeAsync("new@example.com", _sender.LastCode!);

        Assert.Equal(OtpResult.Ok, result);
        Assert.Equal("new@example.com", email);
        Assert.Empty(_players.Items); // no account was created, unlike VerifyOtpAsync
    }

    [Fact]
    public async Task An_address_with_an_existing_account_is_refused_without_creating_or_changing_anything()
    {
        var existing = Player.Register("p-existing", "taken@example.com", "Someone", Language.En, _clock.Now);
        await _players.UpsertAsync(existing);

        var sut = Sut();
        await sut.RequestOtpAsync("taken@example.com", Language.En);
        var (result, email) = await sut.VerifyUpgradeAsync("taken@example.com", _sender.LastCode!);

        Assert.Equal(OtpResult.AddressTaken, result);
        Assert.Null(email);
        Assert.Single(_players.Items);
        Assert.Equal("Someone", _players.Items[0].DisplayName); // untouched
    }

    [Fact]
    public async Task A_wrong_code_refuses_the_upgrade_the_same_way_it_refuses_sign_in()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("amir@example.com", Language.En);

        var (result, email) = await sut.VerifyUpgradeAsync("amir@example.com", "000000");

        Assert.Equal(OtpResult.Wrong, result);
        Assert.Null(email);
    }

    [Fact]
    public async Task Verifying_an_upgrade_without_ever_requesting_a_code_is_rejected()
    {
        var (result, email) = await Sut().VerifyUpgradeAsync("nobody@example.com", "123456");
        Assert.Equal(OtpResult.Expired, result);
        Assert.Null(email);
    }

    /// <summary>The attempt limit is shared machinery (<c>OtpChallenge.Verify</c>), but this proves the
    /// upgrade path actually saves the attempt count back — the same "attempts have to survive the
    /// round trip" requirement <see cref="Attempts_carry_across_calls_so_guessing_is_actually_limited"/>
    /// proves for ordinary sign-in.</summary>
    [Fact]
    public async Task Attempts_carry_across_calls_on_the_upgrade_path_too()
    {
        var sut = Sut();
        await sut.RequestOtpAsync("amir@example.com", Language.En);
        for (var i = 0; i < OtpChallenge.MaxAttempts; i++)
            await sut.VerifyUpgradeAsync("amir@example.com", "000000");

        var (result, _) = await sut.VerifyUpgradeAsync("amir@example.com", _sender.LastCode!);
        Assert.Equal(OtpResult.TooManyAttempts, result);
    }
}
