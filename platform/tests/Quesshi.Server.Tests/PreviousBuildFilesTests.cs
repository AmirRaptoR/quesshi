using Microsoft.AspNetCore.Hosting;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Quesshi.Server;

namespace Quesshi.Server.Tests;

public sealed class PreviousBuildFilesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"quesshi-builds-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Write("current", "index.html", "shell v2");
        Write("current", "_framework/Quesshi.Web.new.wasm", "web new");
        Write("current", "_framework/Quesshi.Web.shared.wasm", "shared current");
        Write("previous", "index.html", "shell v1");
        Write("previous", "_framework/Quesshi.Web.old.wasm", "web old");
        Write("previous", "_framework/Quesshi.Web.Admin.old.wasm", "admin old");
        Write("previous", "_framework/Quesshi.Web.shared.wasm", "shared previous");
        Write("previous", "_content/Quesshi.Kinds.MapPick.Client/maps/world.svg", "map old");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(_root, "current")) });
        _app.UsePreviousBuildFiles(Path.Combine(_root, "current"), Path.Combine(_root, "previous"));
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Serves_current_and_previous_fingerprinted_files_side_by_side()
    {
        Assert.Equal("web new", await _client.GetStringAsync("/_framework/Quesshi.Web.new.wasm"));
        Assert.Equal("web old", await _client.GetStringAsync("/_framework/Quesshi.Web.old.wasm"));
        Assert.Equal("admin old", await _client.GetStringAsync("/_framework/Quesshi.Web.Admin.old.wasm"));
        Assert.Equal("map old", await _client.GetStringAsync("/_content/Quesshi.Kinds.MapPick.Client/maps/world.svg"));
    }

    [Fact]
    public async Task Never_answers_a_file_the_current_build_has_from_the_previous_build()
    {
        Assert.Equal("shell v2", await _client.GetStringAsync("/index.html"));
        Assert.Equal("shared current", await _client.GetStringAsync("/_framework/Quesshi.Web.shared.wasm"));
    }

    [Fact]
    public async Task Does_not_expose_previous_files_outside_the_client_folders()
    {
        Write("previous", "secret.txt", "no");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/secret.txt")).StatusCode);
    }

    private void Write(string build, string path, string content)
    {
        var full = Path.Combine(_root, build, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
