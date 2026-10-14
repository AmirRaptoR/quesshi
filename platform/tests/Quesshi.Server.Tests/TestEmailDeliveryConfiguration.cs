using System.Runtime.CompilerServices;

namespace Quesshi.Server.Tests;

internal static class TestEmailDeliveryConfiguration
{
    [ModuleInitializer]
    public static void ConfigureLoggingDelivery()
    {
        // Test hosts intentionally use the explicit non-SMTP delivery mode. Production
        // startup continues to require SMTP or this same explicit configuration.
        Environment.SetEnvironmentVariable("Smtp__LogInsteadOfSending", "true");
    }
}
