using Microsoft.AspNetCore.Identity;

namespace Quesshi.Infrastructure.Security;

internal interface IAdminPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
    void BurnTime();
}

/// <summary>Uses the platform ASP.NET Identity format and a real decoy hash for unknown accounts.</summary>
internal sealed class IdentityPasswordHasher : IAdminPasswordHasher
{
    private readonly PasswordHasher<object> _hasher = new();
    private readonly string _decoyHash;

    public IdentityPasswordHasher()
    {
        _decoyHash = _hasher.HashPassword(new object(), "a password nobody has");
    }

    public string Hash(string password) => _hasher.HashPassword(new object(), password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(new object(), hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;

    public void BurnTime() => _hasher.VerifyHashedPassword(new object(), _decoyHash, "anything at all");
}
