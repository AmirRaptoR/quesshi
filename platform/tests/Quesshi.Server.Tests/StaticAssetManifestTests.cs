using Microsoft.Extensions.DependencyInjection;
using Quesshi.Infrastructure.Database;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

public sealed class StaticAssetManifestTests
{
    [Fact]
    public void Uses_the_server_static_asset_manifest_instead_of_the_test_host_name()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quesshi-static-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "Quesshi.Server.staticwebassets.endpoints.json");
        File.WriteAllText(manifest, "{}");

        try
        {
            Assert.Equal(manifest, Program.GetStaticAssetsManifestPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Does_not_map_static_assets_when_the_server_manifest_is_unavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quesshi-static-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Null(Program.GetStaticAssetsManifestPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Creates_the_application_from_the_server_manifest_in_testhost()
    {
        await using var sources = new DatabaseSources(
            "Host=127.0.0.1;Database=quesshi;Username=app;Password=test",
            "Host=127.0.0.1;Database=quesshi;Username=system;Password=test");
        await using var app = Program.CreateApplication(sources, 11121, 30121);

        Assert.NotNull(app);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("map_pick")]
    public async Task Registers_each_supported_content_kind(string kind)
    {
        await using var sources = new DatabaseSources(
            "Host=127.0.0.1;Database=quesshi;Username=app;Password=test",
            "Host=127.0.0.1;Database=quesshi;Username=system;Password=test");
        await using var app = Program.CreateApplication(sources, 11121, 30121);

        var registry = app.Services.GetRequiredService<IKindRegistry>();

        Assert.True(registry.TryGetCurrent(kind, out _));
    }

    [Fact]
    public async Task Makes_client_static_assets_available_in_testhost()
    {
        await using var sources = new DatabaseSources(
            "Host=127.0.0.1;Database=quesshi;Username=app;Password=test",
            "Host=127.0.0.1;Database=quesshi;Username=system;Password=test");
        await using var app = Program.CreateApplication(sources, 11122, 30122);

        Assert.True(app.Environment.WebRootFileProvider.GetFileInfo("icon-192.png").Exists);
    }
}
