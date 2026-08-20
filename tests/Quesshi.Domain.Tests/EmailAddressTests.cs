using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class EmailAddressTests
{
    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("  Spaced@Example.COM  ")]
    public void An_ordinary_address_is_accepted(string value)
        => Assert.True(EmailAddress.LooksValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nobody")]
    [InlineData("@example.com")]
    [InlineData("someone@")]
    [InlineData("someone@example")]
    [InlineData("two@at@example.com")]
    [InlineData("has space@example.com")]
    [InlineData("someone@.com")]
    [InlineData("someone@example.")]
    public void Anything_that_could_not_receive_mail_is_refused(string? value)
        => Assert.False(EmailAddress.LooksValid(value));

    [Fact]
    public void An_absurdly_long_address_is_refused()
        => Assert.False(EmailAddress.LooksValid(new string('x', 250) + "@example.com"));

    [Fact]
    public void Normalising_folds_case_and_trims()
        => Assert.Equal("someone@example.com", EmailAddress.Normalise("  SomeOne@Example.CoM "));
}
