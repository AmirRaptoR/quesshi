using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Quesshi.Server;

internal sealed record BuildMetadata(string Service, string Version, string Commit, string BuiltAt)
{
    public static BuildMetadata Development { get; } = new("quesshi", "development", "development", "development");

    public static BuildMetadata FromAssembly() => FromMetadata(
        typeof(BuildMetadata).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>());

    public static BuildMetadata FromMetadata(IEnumerable<AssemblyMetadataAttribute> attributes)
    {
        var values = attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        return values.TryGetValue("QuesshiService", out var service)
            && values.TryGetValue("QuesshiVersion", out var version)
            && values.TryGetValue("QuesshiCommit", out var commit)
            && values.TryGetValue("QuesshiBuiltAt", out var builtAt)
            && !string.IsNullOrWhiteSpace(service)
            && !string.IsNullOrWhiteSpace(version)
            && !string.IsNullOrWhiteSpace(commit)
            && !string.IsNullOrWhiteSpace(builtAt)
            ? new BuildMetadata(service, version, commit, builtAt)
            : Development;
    }
}

internal sealed record HealthResponse(bool Ok, string Service, string Version, string Commit, string BuiltAt);

internal static class VersionEndpoints
{
    public static void MapVersionEndpoints(this IEndpointRouteBuilder app, BuildMetadata metadata)
    {
        app.MapGet("/version", () => Results.Ok(metadata));
        app.MapGet("/health", () => Results.Ok(new HealthResponse(
            true,
            metadata.Service,
            metadata.Version,
            metadata.Commit,
            metadata.BuiltAt)));
    }
}
