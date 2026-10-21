using System.Diagnostics;
using System.Text.Json;

namespace Quesshi.Bench.Live;

public sealed record WorkerRequest(
    string Command,
    string[]? Endpoints = null,
    string? TenantHost = null,
    int Count = 0,
    Guid Session = default,
    LiveScenario Scenario = LiveScenario.Burst,
    DateTimeOffset Deadline = default);

public sealed record WorkerResponse(string? Error = null, Guid[]? PlayerIds = null, CommandSample[]? Samples = null, int Views = 0);

/// <summary>
/// The load generator runs in its own process so its garbage collections, thread pool and sockets cannot pause the
/// silos under test. It answers one JSON request per line on standard input.
/// </summary>
public static class LoadWorkerHost
{
    public const string Argument = "--load-worker";

    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        var clients = new List<BenchClient>();
        var streams = new List<SseSubscription>();
        try
        {
            while (await input.ReadLineAsync() is { } line)
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line)!;
                WorkerResponse response;
                try
                {
                    response = await HandleAsync(request, clients, streams);
                }
                catch (Exception exception)
                {
                    response = new WorkerResponse(Error: exception.ToString());
                }
                await output.WriteLineAsync(JsonSerializer.Serialize(response));
                await output.FlushAsync();
                if (request.Command == "stop") break;
            }
            return 0;
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
            foreach (var client in clients) client.Dispose();
        }
    }

    private static async Task<WorkerResponse> HandleAsync(WorkerRequest request, List<BenchClient> clients, List<SseSubscription> streams)
    {
        switch (request.Command)
        {
            case "setup":
                clients.AddRange(await LoadClients.CreateClientsAsync(request.Count, request.Endpoints!.Select(endpoint => new Uri(endpoint)).ToArray(), request.TenantHost!, CancellationToken.None));
                return new WorkerResponse(PlayerIds: clients.Select(client => client.PlayerId).ToArray());
            case "first":
                await LoadClients.SendCommandAsync(clients[0], request.Session, phase: "0", slot: 0, CancellationToken.None);
                return new WorkerResponse();
            case "answer-all":
                await Task.WhenAll(clients.Take(request.Count).Select(client => LoadClients.SendCommandAsync(client, request.Session, phase: "1", slot: 0, CancellationToken.None)));
                return new WorkerResponse();
            case "streams":
                streams.AddRange(await LoadClients.OpenStreamsAsync(request.Count, clients, request.Session, CancellationToken.None));
                return new WorkerResponse();
            case "burst":
                return new WorkerResponse(Samples: (await LoadClients.SubmitAnswersAsync(request.Scenario, clients, request.Session, request.Deadline, CancellationToken.None)).ToArray());
            case "views":
                return new WorkerResponse(Views: streams.Sum(stream => stream.ViewsReceived));
            case "stop":
                return new WorkerResponse();
            default:
                throw new ArgumentException($"Unknown worker command '{request.Command}'.");
        }
    }
}

/// <summary>The benchmark's side of the worker protocol.</summary>
public sealed class LoadWorkerProcess : IAsyncDisposable
{
    private readonly Process _process;

    private LoadWorkerProcess(Process process) => _process = process;

    public static LoadWorkerProcess Start()
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The benchmark cannot find its own executable.");
        var arguments = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(LoadWorkerProcess).Assembly.Location, LoadWorkerHost.Argument }
            : new[] { LoadWorkerHost.Argument };
        var start = new ProcessStartInfo(host) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new LoadWorkerProcess(Process.Start(start) ?? throw new InvalidOperationException("The load worker did not start."));
    }

    public async Task<WorkerResponse> SendAsync(WorkerRequest request)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await _process.StandardInput.FlushAsync();
        var line = await _process.StandardOutput.ReadLineAsync()
            ?? throw new InvalidOperationException($"The load worker exited during '{request.Command}'.");
        var response = JsonSerializer.Deserialize<WorkerResponse>(line)!;
        return response.Error is null ? response : throw new InvalidOperationException($"The load worker failed '{request.Command}': {response.Error}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) await SendAsync(new WorkerRequest("stop"));
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
