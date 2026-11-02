using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class PackEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new PackHttpFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("switch", "custom_questions", true);
        await _host.ExecuteOwnerAsync("""
            insert into tenant_languages(tenant_id, code, name, english_name, direction, sort_order)
            values (@tenantId, 'en', 'English', 'English', 'ltr', 0)
            """, new { tenantId = _host.TenantId });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Player_creates_renames_describes_and_deletes_their_own_pack()
    {
        var created = await CreatePackAsync(Host.Player, "First", "About it");
        Assert.Equal("First", created.Title);
        Assert.Equal("About it", created.Description);
        Assert.Equal("private", created.Visibility);

        using var renamed = await Host.Player.PutAsJsonAsync($"/api/packs/{created.Id}", new { title = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using var described = await Host.Player.PutAsJsonAsync($"/api/packs/{created.Id}", new { description = "New text" });
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);

        var read = await GetPackAsync(Host.Player, created.Id);
        Assert.Equal("Renamed", read.Title);
        Assert.Equal("New text", read.Description);
        var listed = await Host.Player.GetFromJsonAsync<PackBody[]>("/api/packs");
        Assert.Contains(listed!, pack => pack.Id == created.Id);

        using var deleted = await Host.Player.DeleteAsync($"/api/packs/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await Host.Player.GetAsync($"/api/packs/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [SkippableFact]
    public async Task Player_adds_two_kinds_of_question_and_lists_them_in_order()
    {
        var pack = await CreatePackAsync(Host.Player, "Mixed");
        var choice = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("Capital of France?"));
        var order = await AddQuestionAsync(Host.Player, pack.Id, OrderBody("Sort by size"));

        var listed = await ListQuestionsAsync(Host.Player, pack.Id);

        Assert.Equal([choice.Id, order.Id], listed.Select(question => question.Id));
        Assert.Equal(["choice", "order"], listed.Select(question => question.Kind));
        Assert.Equal([0, 1], listed.Select(question => question.Position));
    }

    [SkippableFact]
    public async Task Player_reorders_updates_and_removes_a_packs_questions()
    {
        var pack = await CreatePackAsync(Host.Player, "Editable");
        var first = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("One?"));
        var second = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("Two?"));
        var third = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("Three?"));

        using var reordered = await Host.Player.PutAsJsonAsync($"/api/packs/{pack.Id}/questions/order", new { questionIds = new[] { third.Id, first.Id, second.Id } });
        Assert.Equal(HttpStatusCode.NoContent, reordered.StatusCode);
        Assert.Equal([third.Id, first.Id, second.Id], (await ListQuestionsAsync(Host.Player, pack.Id)).Select(question => question.Id));

        using var updated = await Host.Player.PutAsJsonAsync($"/api/packs/{pack.Id}/questions/{first.Id}", ChoiceBody("One, edited?"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("One, edited?", (await ListQuestionsAsync(Host.Player, pack.Id)).Single(question => question.Id == first.Id).Prompt);

        using var removed = await Host.Player.DeleteAsync($"/api/packs/{pack.Id}/questions/{first.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var remaining = await ListQuestionsAsync(Host.Player, pack.Id);
        Assert.Equal([third.Id, second.Id], remaining.Select(question => question.Id));
        Assert.Equal([0, 1], remaining.Select(question => question.Position));
    }

    [SkippableTheory]
    [InlineData("private")]
    [InlineData("unlisted")]
    [InlineData("tenant")]
    public async Task Player_sets_pack_visibility_through_the_pack_route(string visibility)
    {
        var pack = await CreatePackAsync(Host.Player, "Visible");

        using var response = await Host.Player.PutAsJsonAsync($"/api/packs/{pack.Id}", new { visibility });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(visibility, (await GetPackAsync(Host.Player, pack.Id)).Visibility);
        Assert.Equal(visibility, await Host.QueryOwnerAsync<string>("select visibility from packs where id = @id", new { id = pack.Id }) is [var stored] ? stored : null);
    }

    [SkippableFact]
    public async Task Player_rejects_an_unknown_visibility()
    {
        var pack = await CreatePackAsync(Host.Player, "Odd");

        using var response = await Host.Player.PutAsJsonAsync($"/api/packs/{pack.Id}", new { visibility = "public" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Every_pack_route_refuses_a_guest()
    {
        var pack = await CreatePackAsync(Host.Player, "Not for guests");
        var question = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("Guest?"));
        await Host.Player.PutAsJsonAsync($"/api/packs/{pack.Id}", new { visibility = "tenant" });

        foreach (var (method, path, body) in Routes(pack.Id, question.Id))
        {
            using var response = await Send(Host.Guest, method, path, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode} for a guest.");
        }
    }

    [SkippableFact]
    public async Task Every_pack_editing_route_is_refused_when_custom_questions_is_off()
    {
        var pack = await CreatePackAsync(Host.Player, "Frozen");
        var question = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("Frozen?"));
        await Host.SetCapabilityAsync("switch", "custom_questions", false);
        try
        {
            foreach (var (method, path, body) in Routes(pack.Id, question.Id).Where(route => route.Method != HttpMethod.Get && route.Path != "/api/packs"))
            {
                using var response = await Send(Host.Player, method, path, body);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode} with custom_questions off.");
            }

            var unchanged = await GetPackAsync(Host.Player, pack.Id);
            Assert.Equal("Frozen", unchanged.Title);
            Assert.Single(await ListQuestionsAsync(Host.Player, pack.Id));
        }
        finally
        {
            await Host.SetCapabilityAsync("switch", "custom_questions", true);
        }
    }

    [SkippableTheory]
    [InlineData("tenant")]
    [InlineData("unlisted")]
    [InlineData("private")]
    public async Task Another_player_cannot_edit_or_delete_the_owners_pack(string visibility)
    {
        var pack = await CreatePackAsync(Host.Player, "Owned");
        var question = await AddQuestionAsync(Host.Player, pack.Id, ChoiceBody("Mine?"));
        await Host.Player.PutAsJsonAsync($"/api/packs/{pack.Id}", new { visibility });
        var (other, _) = await Host.CreateSecondPlayerClientAsync();

        foreach (var (method, path, body) in Routes(pack.Id, question.Id).Where(route => route.Method != HttpMethod.Get && route.Path != "/api/packs"))
        {
            using var response = await Send(other, method, path, body);
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{method} {path} returned {(int)response.StatusCode} for another player.");
        }

        var intact = await GetPackAsync(Host.Player, pack.Id);
        Assert.Equal("Owned", intact.Title);
        Assert.Equal(visibility, intact.Visibility);
        Assert.Equal([question.Id], (await ListQuestionsAsync(Host.Player, pack.Id)).Select(item => item.Id));
    }

    private static IEnumerable<(HttpMethod Method, string Path, object? Body)> Routes(Guid pack, Guid question) =>
    [
        (HttpMethod.Get, "/api/packs", null),
        (HttpMethod.Post, "/api/packs", new { title = "New" }),
        (HttpMethod.Get, $"/api/packs/{pack}", null),
        (HttpMethod.Put, $"/api/packs/{pack}", new { title = "Hijacked", visibility = "private" }),
        (HttpMethod.Delete, $"/api/packs/{pack}", null),
        (HttpMethod.Get, $"/api/packs/{pack}/questions", null),
        (HttpMethod.Post, $"/api/packs/{pack}/questions", ChoiceBody("Injected?")),
        (HttpMethod.Put, $"/api/packs/{pack}/questions/order", new { questionIds = new[] { question } }),
        (HttpMethod.Put, $"/api/packs/{pack}/questions/{question}", ChoiceBody("Rewritten?")),
        (HttpMethod.Delete, $"/api/packs/{pack}/questions/{question}", null),
    ];

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static object ChoiceBody(string prompt) => new
    {
        kind = "choice",
        prompt,
        payload = new { options = new[] { "A", "B", "C", "D" }, correctIndex = 1 },
    };

    private static object OrderBody(string prompt) => new
    {
        kind = "order",
        prompt,
        payload = new { items = new[] { new { id = "a", text = "Ant" }, new { id = "b", text = "Cat" }, new { id = "c", text = "Dog" }, new { id = "d", text = "Elk" } } },
    };

    private static async Task<PackBody> CreatePackAsync(HttpClient client, string title, string? description = null)
    {
        using var response = await client.PostAsJsonAsync("/api/packs", new { title, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PackBody>())!;
    }

    private static async Task<PackBody> GetPackAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PackBody>($"/api/packs/{id}"))!;

    private static async Task<QuestionBody> AddQuestionAsync(HttpClient client, Guid pack, object body)
    {
        using var response = await client.PostAsJsonAsync($"/api/packs/{pack}/questions", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuestionBody>())!;
    }

    private static async Task<QuestionBody[]> ListQuestionsAsync(HttpClient client, Guid pack) =>
        (await client.GetFromJsonAsync<QuestionBody[]>($"/api/packs/{pack}/questions"))!;

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private sealed record PackBody(Guid Id, string Title, string? Description, string Language, string Visibility);

    private sealed record QuestionBody(Guid Id, int Position, string Kind, string Prompt);

    private sealed class PackHttpFeature : IFeature
    {
        public string Id => "pack_http";
        public FeatureSettings Settings => FeatureSettings.Create(new PackHttpSettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
        }
    }

    private sealed record PackHttpSettings;
}
