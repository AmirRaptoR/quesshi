using System.Security.Cryptography;

namespace Quesshi.Application.Sessions;

internal interface ISessionCodeGenerator
{
    string Next();
}

internal sealed class SessionCodeGenerator : ISessionCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string Next()
    {
        Span<char> code = stackalloc char[6];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(code);
    }
}
