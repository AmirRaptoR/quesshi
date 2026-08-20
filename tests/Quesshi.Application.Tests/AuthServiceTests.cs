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
}
