namespace Quesshi.Application.Ports;

/// <summary>
/// Hashing lives behind a port so the Domain never depends on a crypto library, and so the
/// algorithm can be upgraded without touching the rules that use it.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);

    /// <summary>
    /// Runs the same work as a real verification against a throwaway hash. Used when the account
    /// does not exist, so that "no such user" and "wrong password" take the same time to answer.
    /// </summary>
    void BurnTime();
}
