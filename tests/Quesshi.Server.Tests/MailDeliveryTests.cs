using Quesshi.Infrastructure.Otp;

namespace Quesshi.Server.Tests;

public class MailDeliveryTests
{
    private const bool Development = true;
    private const bool Deployed = false;

    [Fact]
    public void A_configured_host_is_delivered_by_smtp_wherever_it_runs()
    {
        var options = new SmtpOptions { Host = "smtp.example.com" };
        Assert.Equal(MailDelivery.Smtp, options.Delivery(Development));
        Assert.Equal(MailDelivery.Smtp, options.Delivery(Deployed));
    }

    [Fact]
    public void A_host_still_wins_when_the_machine_has_asked_for_the_log()
    {
        var options = new SmtpOptions { Host = "smtp.example.com", LogInsteadOfSending = true };
        Assert.Equal(MailDelivery.Smtp, options.Delivery(Deployed));
    }

    [Fact]
    public void A_development_machine_with_no_host_reads_the_code_in_its_log()
    {
        var options = new SmtpOptions { Host = "" };
        Assert.Equal(MailDelivery.Log, options.Delivery(Development));
    }

    [Fact]
    public void A_deployment_may_log_the_code_but_only_by_saying_so()
    {
        var options = new SmtpOptions { Host = "", LogInsteadOfSending = true };
        Assert.Equal(MailDelivery.Log, options.Delivery(Deployed));
    }

    // The finding this file exists for: losing Smtp:Host on a server used to be indistinguishable
    // from choosing to log, and the difference is a log full of usable credentials.
    [Fact]
    public void A_deployment_that_merely_lost_its_host_is_unconfigured_rather_than_logging()
    {
        var options = new SmtpOptions { Host = "" };
        Assert.Equal(MailDelivery.Unconfigured, options.Delivery(Deployed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_host_of_any_shape_counts_as_no_host(string? host)
    {
        var options = new SmtpOptions { Host = host };
        Assert.Equal(MailDelivery.Unconfigured, options.Delivery(Deployed));
        Assert.Equal(MailDelivery.Log, options.Delivery(Development));
    }
}
