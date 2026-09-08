using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

/// <summary>The in-memory twin of <c>Quesshi.Application.Tests.InMemoryOtpStore</c> — that one lives in
/// a different test assembly, so the guest-upgrade HTTP tests (issue #55) need their own copy rather
/// than a cross-project reference just for this.</summary>
public sealed class FakeOtpStore : IOtpStore
{
    private readonly Dictionary<string, OtpChallenge> _items = [];
    private readonly object _lock = new();

    public Task SaveAsync(OtpChallenge challenge, CancellationToken ct = default)
    {
        lock (_lock) _items[challenge.Email] = challenge;
        return Task.CompletedTask;
    }

    public Task<OtpChallenge?> GetAsync(string email, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_items.GetValueOrDefault(email.Trim().ToLowerInvariant()));
    }

    public Task DeleteAsync(string email, CancellationToken ct = default)
    {
        lock (_lock) _items.Remove(email.Trim().ToLowerInvariant());
        return Task.CompletedTask;
    }
}

/// <summary>Hands the code straight back rather than mailing or logging it — nothing under test ever
/// exercises <c>AuthService.RequestOtpAsync</c> through this host, but <c>AuthService</c> still needs
/// an <see cref="IOtpSender"/> to construct, and the guest-upgrade tests build their own challenges
/// directly against <see cref="FakeOtpStore"/> rather than going through it.</summary>
public sealed class FakeOtpSender : IOtpSender
{
    public Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default)
        => Task.FromResult<string?>(code);
}
