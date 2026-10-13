using System.Globalization;

namespace Quesshi.Application.Tenancy;

/// <summary>A tenant selected from a canonical request host.</summary>
public sealed record Tenant(Guid Id, string Slug, string Name, string DefaultLanguage);

/// <summary>Resolves tenants without introducing a current or fallback tenant.</summary>
public interface ITenants
{
    Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>Canonicalizes host input to the form stored in <c>tenant_hosts</c>.</summary>
internal static class TenantHost
{
    public static string Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";

        var value = host.Trim();
        if (value.Contains("://", StringComparison.Ordinal)
            || value.IndexOfAny(['/', '?', '#', '@', '[', ']']) >= 0
            || value.Any(char.IsControl)) return "";

        var colon = value.LastIndexOf(':');
        var hostname = colon < 0 ? value : value[..colon];
        var port = colon < 0 ? "" : value[colon..];
        if (colon >= 0 && (hostname.Contains(':') || !int.TryParse(port[1..], out var portNumber) || portNumber is < 0 or > 65535)) return "";

        hostname = hostname.TrimEnd('.');
        if (!Uri.TryCreate($"http://{hostname}{port}", UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.PathAndQuery is not "/"
            || !string.IsNullOrEmpty(uri.Fragment)) return "";

        try
        {
            return new IdnMapping().GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "";
        }
    }
}
