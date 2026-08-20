using Microsoft.AspNetCore.Identity;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Security;

/// <summary>
/// ASP.NET Identity's PBKDF2 hasher: salted, iterated, and versioned, so the work factor can be
/// raised later without invalidating existing hashes. Not hand-rolled, on purpose.
/// </summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<AdminUser> _hasher = new();
    private readonly string _decoyHash;

    public IdentityPasswordHasher()
    {
        // A real hash to verify against when the account does not exist, so the timing matches.
        _decoyHash = _hasher.HashPassword(null!, "a password nobody has");
    }

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password)
        => _hasher.VerifyHashedPassword(null!, hash, password) is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;

    public void BurnTime() => _hasher.VerifyHashedPassword(null!, _decoyHash, "anything at all");
}
