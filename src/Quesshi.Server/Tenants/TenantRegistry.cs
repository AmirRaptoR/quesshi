using Quesshi.Application.Ports;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;

namespace Quesshi.Server.Tenants;

/// <summary>Validated, immutable host-to-tenant configuration. Hostnames never define tenant IDs.</summary>
public sealed class TenantRegistry
{
    private readonly IReadOnlyDictionary<string, TenantSettingsDto> tenantsByHost;
    private readonly IReadOnlySet<string> tenantIds;

    public TenantRegistry(TenantOptions options)
    {
        var map = new Dictionary<string, TenantSettingsDto>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in options.Tenants)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Name))
                throw new InvalidOperationException("Every tenant requires a stable Id and a display Name.");
            if (!Regex.IsMatch(definition.Id, "^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException($"Tenant Id '{definition.Id}' must use lowercase letters, digits, hyphens or underscores.");
            if (!ids.Add(definition.Id))
                throw new InvalidOperationException($"Tenant Id '{definition.Id}' is configured more than once.");
            if (definition.Hosts.Count == 0)
                throw new InvalidOperationException($"Tenant '{definition.Id}' must have at least one host mapping.");

            var settings = new TenantSettingsDto(definition.Id, definition.Name,
                new TenantBrandSettingsDto(definition.Theme,
                    new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(definition.LandingContent, StringComparer.Ordinal)),
                    Array.AsReadOnly(definition.Languages.ToArray()), Array.AsReadOnly(definition.EnabledModes.ToArray()),
                    new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(definition.Rules, StringComparer.Ordinal))));
            foreach (var host in definition.Hosts)
            {
                var normalized = NormalizeHost(host);
                if (!map.TryAdd(normalized, settings))
                    throw new InvalidOperationException($"Host '{normalized}' is mapped to more than one tenant.");
            }
        }

        tenantsByHost = map;
        tenantIds = ids;
    }

    public bool ContainsTenant(string tenantId) => tenantIds.Contains(tenantId);

    public bool TryResolve(string host, out TenantSettingsDto settings)
        => tenantsByHost.TryGetValue(NormalizeHost(host), out settings!);

    public static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        // Request.Host.Host excludes the port. Also tolerate configured host:port values.
        var value = host.Trim().TrimEnd('.');
        if (Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri)) value = uri.Host;
        return value.ToLowerInvariant();
    }
}
