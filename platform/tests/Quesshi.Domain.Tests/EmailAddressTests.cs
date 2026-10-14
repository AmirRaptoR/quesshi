using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("  Person@Example.COM  ", "person@example.com")]
    [InlineData("person@example.com", "person@example.com")]
    public void Valid_addresses_are_normalized(string input, string expected)
    {
        Assert.True(EmailAddress.IsValid(input));
        Assert.Equal(expected, EmailAddress.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("person@localhost")]
    [InlineData("person@@example.com")]
    [InlineData("person @example.com")]
    public void Invalid_addresses_are_refused(string input) => Assert.False(EmailAddress.IsValid(input));

    [Fact]
    public void Codes_are_six_numeric_digits()
    {
        var code = OtpCode.Create();

        Assert.True(OtpCode.IsValid(code));
        Assert.Equal(OtpCode.Length, code.Length);
    }
}
