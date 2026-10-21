using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Quesshi.Bench.Live;

/// <summary>The players: guest accounts that talk to the silos only through the production HTTP and SSE routes.</summary>
internal static class LoadClients
{
    public static async Task<List<BenchClient>> CreateClientsAsync(
        int playerCount,
        IReadOnlyList<Uri> endpoints,
        string tenantHost,
        CancellationToken cancellationToken)
    {
        var players = new List<BenchClient>(playerCount);
        for (var index = 0; index < playerCount; index++)
        {
            // Streams intentionally stay open throughout the question. Command deadlines
            // are enforced per request so they cannot also abort an SSE subscription.
            var http = new HttpClient { BaseAddress = endpoints[index % endpoints.Count], Timeout = Timeout.InfiniteTimeSpan };
            var token = await GetAntiforgeryTokenAsync(http, tenantHost, cookie: null, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/guest")
            {
                Content = JsonContent.Create(new { displayName = $"bench-player-{index:D4}" }),
            };
            request.Headers.Host = tenantHost;
            request.Headers.Add("Origin", $"http://{tenantHost}");
            request.Headers.Add("X-CSRF", token);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var identity = await response.Content.ReadFromJsonAsync<GuestIdentity>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Guest endpoint did not return an identity.");
            var cookie = response.Headers.GetValues("Set-Cookie").SingleOrDefault(value => value.StartsWith("__Host-qp=", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Guest endpoint did not set the profile cookie.");
            var value = WebUtility.UrlDecode(cookie["__Host-qp=".Length..].Split(';')[0]);
            var playerToken = await GetAntiforgeryTokenAsync(http, tenantHost, value, cancellationToken);
            players.Add(new BenchClient(http, tenantHost, value, playerToken, identity.Id));
        }

        return players;
    }

    internal static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string tenantHost, string? cookie, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
        request.Headers.Host = tenantHost;
        if (cookie is not null) request.Headers.Add("Cookie", $"__Host-qp={cookie}");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Antiforgery endpoint returned an empty token.");
    }


    internal static async Task SendCommandAsync(BenchClient client, Guid sessionId, string phase, int slot, CancellationToken cancellationToken)
    {
        using var request = BuildAnswerRequest(client, sessionId, phase, slot);
        using var response = await client.Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    internal static async Task<List<CommandSample>> SubmitAnswersAsync(
        LiveScenario scenario,
        IReadOnlyList<BenchClient> clients,
        Guid sessionId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var work = clients.Select((client, index) => SubmitOneAsync(client, sessionId, deadline, scenario, index, clients.Count, cancellationToken));
        return (await Task.WhenAll(work)).ToList();
    }

    internal static async Task<CommandSample> SubmitOneAsync(
        BenchClient client,
        Guid sessionId,
        DateTimeOffset deadline,
        LiveScenario scenario,
        int index,
        int count,
        CancellationToken cancellationToken)
    {
        var target = scenario == LiveScenario.Spread
            ? deadline.Add(-BenchLiveFormat.Question + TimeSpan.FromMilliseconds(250 + ((BenchLiveFormat.Question.TotalMilliseconds - 500) * index / Math.Max(1, count - 1))))
            : deadline.AddMilliseconds(-250 + ((200d * index) / Math.Max(1, count - 1)));
        var delay = target - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        var sentAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = BuildAnswerRequest(client, sessionId, "1", 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.Http.SendAsync(request, timeout.Token);
            var code = response.Headers.TryGetValues("X-Quesshi-Command-Code", out var values) ? values.SingleOrDefault() : null;
            var receivedAt = response.Headers.TryGetValues("X-Quesshi-Command-Received-At", out var receivedValues)
                && DateTimeOffset.TryParse(receivedValues.SingleOrDefault(), out var parsedReceipt)
                    ? parsedReceipt
                    : (DateTimeOffset?)null;
            var outcome = code is null || receivedAt is null
                ? CommandOutcome.Failed
                : BenchmarkMeasurementClassifier.ResponseOutcome(code, response.IsSuccessStatusCode);
            var error = outcome == CommandOutcome.Accepted
                ? null
                : code is null
                    ? $"http:{(int)response.StatusCode}:missing-command-code"
                    : receivedAt is null
                        ? $"http:{(int)response.StatusCode}:command:{code}:missing-receipt"
                        : $"http:{(int)response.StatusCode}:command:{code}";
            return new CommandSample(client.PlayerId, sentAt, receivedAt, stopwatch.Elapsed, outcome, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandSample(client.PlayerId, sentAt, null, stopwatch.Elapsed, CommandOutcome.TimedOut, "timeout:command-request");
        }
        catch (Exception exception)
        {
            return new CommandSample(client.PlayerId, sentAt, null, stopwatch.Elapsed, CommandOutcome.Failed, $"exception:{exception.GetType().Name}");
        }
    }

    internal static HttpRequestMessage BuildAnswerRequest(BenchClient client, Guid sessionId, string phase, int slot)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId:D}/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "answer",
                at = new { phase, slot, step = 0 },
                payload = new { index = 0 },
            }),
        };
        request.Headers.Host = client.TenantHost;
        request.Headers.Add("Cookie", $"__Host-qp={client.Cookie}");
        request.Headers.Add("Origin", $"http://{client.TenantHost}");
        request.Headers.Add("X-CSRF", client.AntiforgeryToken);
        return request;
    }

    internal static async Task<List<SseSubscription>> OpenStreamsAsync(
        int streamCount,
        IReadOnlyList<BenchClient> clients,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var opens = Enumerable.Range(0, streamCount).Select(async index =>
        {
            var client = clients[index % clients.Count];
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/stream?session={sessionId:D}");
            request.Headers.Host = client.TenantHost;
            request.Headers.Add("Cookie", $"__Host-qp={client.Cookie}");
            var response = await client.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            try
            {
                response.EnsureSuccessStatusCode();
                return await SseSubscription.CreateAsync(response, cancellationToken);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        });

        return [.. await Task.WhenAll(opens)];
    }

}

internal sealed record GuestIdentity(Guid Id);

internal sealed class BenchClient(HttpClient http, string tenantHost, string cookie, string antiforgeryToken, Guid playerId) : IDisposable
{
    public HttpClient Http { get; } = http;
    public string TenantHost { get; } = tenantHost;
    public string Cookie { get; } = cookie;
    public string AntiforgeryToken { get; } = antiforgeryToken;
    public Guid PlayerId { get; } = playerId;
    public void Dispose() => Http.Dispose();
}


internal sealed class SseSubscription : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private int _viewsReceived;

    private SseSubscription(HttpResponseMessage response)
    {
        _response = response;
        _pump = PumpAsync();
    }

    public int ViewsReceived => Volatile.Read(ref _viewsReceived);

    public static async Task<SseSubscription> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var subscription = new SseSubscription(response);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (subscription.ViewsReceived == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        return subscription;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _response.Dispose();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException)
        {
            // Closing an SSE response is the expected way to end its read loop.
        }
        catch (IOException)
        {
            // The response can be cut off after the client has disposed it.
        }

        _stopping.Dispose();
    }

    private async Task PumpAsync()
    {
        await using var stream = await _response.Content.ReadAsStreamAsync(_stopping.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(_stopping.Token) is { } line)
        {
            if (line.Equals("event: view", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _viewsReceived);
            }
        }
    }
}

