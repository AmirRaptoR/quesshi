using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quesshi.Server;

namespace Quesshi.Server.Tests;

public sealed class VersionEndpointsTests
{
    private static readonly BuildMetadata Release = new(
        "quesshi",
        "0.1.0+12.0123456",
        "0123456789abcdef0123456789abcdef01234567",
        "2026-10-15T17:32:00Z");

    [Fact]
    public async Task Version_returns_the_baked_release_metadata()
    {
        await using var host = new VersionTestHost(Release);

        var response = await host.Client.GetAsync("/version");

        response.EnsureSuccessStatusCode();
        Assert.Equal(Release, await response.Content.ReadFromJsonAsync<BuildMetadata>());
    }

    [Fact]
    public async Task Health_keeps_ok_and_returns_the_same_baked_release_metadata()
    {
        await using var host = new VersionTestHost(Release);

        var response = await host.Client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            new HealthResponse(true, Release.Service, Release.Version, Release.Commit, Release.BuiltAt),
            await response.Content.ReadFromJsonAsync<HealthResponse>());
    }

    [Fact]
    public async Task Version_reports_development_when_metadata_is_absent()
    {
        await using var host = new VersionTestHost(BuildMetadata.Development);

        var version = await host.Client.GetAsync("/version");

        version.EnsureSuccessStatusCode();
        Assert.Equal(BuildMetadata.Development, await version.Content.ReadFromJsonAsync<BuildMetadata>());
    }

    [Fact]
    public async Task Health_reports_development_when_metadata_is_absent()
    {
        await using var host = new VersionTestHost(BuildMetadata.Development);

        var health = await host.Client.GetAsync("/health");

        health.EnsureSuccessStatusCode();
        Assert.Equal(
            new HealthResponse(true, "quesshi", "development", "development", "development"),
            await health.Content.ReadFromJsonAsync<HealthResponse>());
    }

    [Fact]
    public void Missing_build_metadata_reports_development()
    {
        Assert.Equal(BuildMetadata.Development, BuildMetadata.FromMetadata([]));
    }

    [Fact]
    public void Incomplete_build_metadata_never_reports_a_stale_release()
    {
        Assert.Equal(BuildMetadata.Development, BuildMetadata.FromMetadata([
            new("QuesshiService", "quesshi"),
            new("QuesshiVersion", Release.Version),
            new("QuesshiCommit", Release.Commit)
        ]));
    }

    [Fact]
    public void Complete_build_metadata_is_preserved()
    {
        Assert.Equal(Release, BuildMetadata.FromMetadata([
            new("QuesshiService", Release.Service),
            new("QuesshiVersion", Release.Version),
            new("QuesshiCommit", Release.Commit),
            new("QuesshiBuiltAt", Release.BuiltAt)
        ]));
    }

    [Fact]
    public async Task Server_assembly_metadata_is_served_by_the_version_route()
    {
        var expected = ExpectedBuildMetadata();
        var metadata = BuildMetadata.FromAssembly();
        await using var host = new VersionTestHost(metadata);

        var response = await host.Client.GetAsync("/version");

        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, metadata);
        Assert.Equal(expected, await response.Content.ReadFromJsonAsync<BuildMetadata>());
    }

    private static BuildMetadata ExpectedBuildMetadata()
    {
        var values = typeof(VersionEndpointsTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);

        return new BuildMetadata(
            values["ExpectedQuesshiService"]!,
            values["ExpectedQuesshiVersion"]!,
            values["ExpectedQuesshiCommit"]!,
            values["ExpectedQuesshiBuiltAt"]!);
    }

    private sealed class VersionTestHost : IAsyncDisposable
    {
        private readonly IHost _host;

        public VersionTestHost(BuildMetadata metadata)
        {
            _host = new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services => services.AddRouting());
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapVersionEndpoints(metadata));
                    });
                })
                .Start();
            Client = _host.GetTestServer().CreateClient();
        }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
