using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Quesshi.Infrastructure.Otp;

namespace Quesshi.Server.Tests;

public sealed class EmailAuthDeliveryTests
{
    [Fact]
    public void Production_requires_explicit_delivery_but_smtp_takes_precedence()
    {
        Assert.Throws<InvalidOperationException>(() => EmailCodeDelivery.Validate(Configuration(), new TestHostEnvironment("Production")));

        EmailCodeDelivery.Validate(Configuration(("Smtp:LogInsteadOfSending", "true")), new TestHostEnvironment("Production"));
        EmailCodeDelivery.Validate(Configuration(("Smtp:Host", "mail.example.test")), new TestHostEnvironment("Production"));
        EmailCodeDelivery.Validate(Configuration(), new TestHostEnvironment("Development"));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value)).Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Quesshi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
