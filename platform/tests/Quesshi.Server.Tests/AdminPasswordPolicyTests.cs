using Quesshi.Application.Identity;

namespace Quesshi.Server.Tests;

public sealed class AdminPasswordPolicyTests
{
    [Theory]
    [InlineData("short", "password.tooShort")]
    [InlineData("password123", "password.tooObvious")]
    [InlineData("x", "password.tooShort")]
    public void Rejects_short_and_blocklisted_passwords(string password, string problem)
    {
        Assert.Contains(problem, AdminPasswordPolicy.Problems(password));
    }

    [Fact]
    public void Accepts_a_long_non_blocklisted_password_and_rejects_more_than_200_characters()
    {
        Assert.Empty(AdminPasswordPolicy.Problems("Fjord!saffron-lantern-2026"));
        Assert.Contains("password.tooLong", AdminPasswordPolicy.Problems(new string('a', 201)));
    }
}
