using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Testing;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Kinds;
using Quesshi.Kinds.Choice;
using Quesshi.Shared;

namespace Quesshi.Bench.Live;

/// <summary>
/// Runs the live benchmark through the same HTTP command and SSE routes used by a player.
/// Database setup is deliberately outside the timed section; all timed session work goes through
/// the application pool and the two independently hosted Orleans silos.
/// </summary>
public sealed class LiveBenchmarkRunner
{
    private const string OwnerUser = "quesshi_owner";
    private const string AppUser = "quesshi_app";
    private const string SystemUser = "quesshi_system";
    private const string RolePassword = "quesshi-benchmark-only";

    public async Task<BenchmarkReport> RunAsync(BenchmarkOptions options, CancellationToken cancellationToken = default)
    {
        var configuredConnection = options.PostgreSql
            ?? Environment.GetEnvironmentVariable("QUESSHI_BENCH_POSTGRES")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Owner");
        var administrationConnection = configuredConnection
            ?? Environment.GetEnvironmentVariable("QUESSHI_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(administrationConnection))
        {
            throw new InvalidOperationException(
                "Set QUESSHI_BENCH_POSTGRES (an owner PostgreSQL connection string) before running the live benchmark.");
        }
        if (!options.DisposablePostgreSql
            && !string.Equals(Environment.GetEnvironmentVariable("QUESSHI_BENCH_DISPOSABLE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The benchmark changes its disposable database roles. Pass --allow-disposable-postgres only for an isolated PostgreSQL server.");
        }

        var runs = options.Matrix
            ? from scenario in Enum.GetValues<LiveScenario>()
              from players in BenchmarkOptions.PlayerCounts
              select options with { Players = players, Scenario = scenario, Matrix = false }
            : [options];

        var reports = new List<ScenarioReport>();
        string? postgresVersion = null;
        foreach (var run in runs)
        {
            ScenarioOutcome outcome;
            try
            {
                outcome = await RunOneAsync(run, administrationConnection, cancellationToken);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Benchmark scenario {run.Scenario} with {run.Players} players failed: {exception.Message}",
                    exception);
            }
            reports.Add(outcome.Report);
            postgresVersion ??= outcome.PostgreSqlVersion;
        }

        return new BenchmarkReport(
            SourceRevision(),
            DateTimeOffset.UtcNow,
            CommandLine(options),
            DescribeEnvironment(administrationConnection, postgresVersion),
            reports);
    }

    private static async Task<ScenarioOutcome> RunOneAsync(
        BenchmarkOptions options,
        string administrationConnection,
        CancellationToken cancellationToken)
    {
        await using var database = await BenchmarkDatabase.CreateAsync(administrationConnection, cancellationToken);
        var measurements = new BenchmarkMeasurements();
        var formats = new FormatRegistry([new BenchLiveFormat()]);
        var kinds = new KindRegistry([new ChoiceKind()]);
        var firstPorts = HostPorts.Allocate();
        var secondPorts = HostPorts.Allocate();
        var firstHttp = HostPorts.AllocateHttp();
        var secondHttp = HostPorts.AllocateHttp();
        var priorJobs = Environment.GetEnvironmentVariable("Jobs__Enabled");
        Environment.SetEnvironmentVariable("Jobs__Enabled", "false");

        DatabaseSources? firstSources = null;
        DatabaseSources? secondSources = null;
        WebApplication? first = null;
        WebApplication? second = null;
        var clients = new List<BenchClient>();
        var streams = new List<SseSubscription>();
        try
        {
            var tenant = await BenchmarkFixture.SeedTenantAsync(database.OwnerConnectionString, database.TenantHost, cancellationToken);

            firstSources = new DatabaseSources(database.AppConnectionString, database.SystemConnectionString, options.ApplicationPoolSize, measurements);
            // Both silos share one application data source so the pool-10 scenario is
            // genuinely capped at ten connections across the complete live path.
            secondSources = firstSources;
            first = await StartHostAsync(firstSources, firstPorts, firstHttp, formats, kinds, measurements, cancellationToken);
            second = await StartHostAsync(secondSources, secondPorts, secondHttp, formats, kinds, measurements, cancellationToken);

            var endpoints = new[]
            {
                new Uri($"http://127.0.0.1:{firstHttp}"),
                new Uri($"http://127.0.0.1:{secondHttp}"),
            };
            clients = await CreateClientsAsync(options.Players, endpoints, database.TenantHost, cancellationToken);
            var session = await BenchmarkFixture.SeedSessionAsync(database.OwnerConnectionString, tenant, options, clients.Select(client => client.PlayerId).ToArray(), cancellationToken);

            // The first real client command activates the grain and begins the shared
            // countdown. Open the stream cohort during that countdown so setup cannot
            // consume the question window that is under measurement.
            await SendCommandAsync(clients[0], session.Id, phase: "0", slot: 0, cancellationToken);
            if (options.StreamCount > 0)
            {
                streams = await OpenStreamsAsync(options.StreamCount, clients, session.Id, cancellationToken);
            }
            await WaitForPhaseAsync(database.OwnerConnectionString, session.Id, "1", cancellationToken);

            // Login, seed, countdown, and initial view traffic are not benchmark samples. The
            // first command remains a distinct activation measurement for the cold scenario.
            var coldActivation = options.Scenario == LiveScenario.Cold
                ? measurements.Samples.Where(sample => sample.Kind == SessionTurnKind.ColdActivation).ToArray()
                : [];
            var coldPoolWaits = options.Scenario == LiveScenario.Cold ? measurements.PoolWaits.ToArray() : [];
            measurements.Reset();
            var deadline = await GetDeadlineAsync(database.OwnerConnectionString, session.Id, cancellationToken);
            var commands = await SubmitAnswersAsync(options, clients, session.Id, deadline, cancellationToken);
            var final = await CollectResultAsync(database.OwnerConnectionString, session, options, deadline, commands, measurements, coldActivation, coldPoolWaits, streams, cancellationToken);
            return new ScenarioOutcome(final, database.PostgreSqlVersion);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }

            foreach (var client in clients)
            {
                client.Dispose();
            }

            if (second is not null)
            {
                await second.StopAsync(CancellationToken.None);
                await second.DisposeAsync();
            }

            if (first is not null)
            {
                await first.StopAsync(CancellationToken.None);
                await first.DisposeAsync();
            }

            if (secondSources is not null && !ReferenceEquals(secondSources, firstSources))
            {
                await secondSources.DisposeAsync();
            }

            if (firstSources is not null)
            {
                await firstSources.DisposeAsync();
            }

            Environment.SetEnvironmentVariable("Jobs__Enabled", priorJobs);
        }
    }

    private static async Task<WebApplication> StartHostAsync(
        DatabaseSources sources,
        HostPorts ports,
        int httpPort,
        IFormatRegistry formats,
        IKindRegistry kinds,
        ISessionTurnObserver measurements,
        CancellationToken cancellationToken)
    {
        var app = Quesshi.Server.Program.CreateApplication(
            sources,
            ports.Silo,
            ports.Gateway,
            configureLogging: logging => logging.ClearProviders(),
            configuredFormats: formats,
            configuredKinds: kinds,
            turnObserver: measurements,
            environmentName: "Development");
        app.Urls.Add($"http://127.0.0.1:{httpPort}");
        await app.Services.GetRequiredService<PostgresClock>().StartAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static async Task<List<BenchClient>> CreateClientsAsync(
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

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string tenantHost, string? cookie, CancellationToken cancellationToken)
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

    private static async Task SendCommandAsync(BenchClient client, Guid sessionId, string phase, int slot, CancellationToken cancellationToken)
    {
        using var request = BuildAnswerRequest(client, sessionId, phase, slot);
        using var response = await client.Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<List<CommandSample>> SubmitAnswersAsync(
        BenchmarkOptions options,
        IReadOnlyList<BenchClient> clients,
        Guid sessionId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var work = clients.Select((client, index) => SubmitOneAsync(client, sessionId, deadline, options.Scenario, index, clients.Count, cancellationToken));
        return (await Task.WhenAll(work)).ToList();
    }

    private static async Task<CommandSample> SubmitOneAsync(
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

    private static HttpRequestMessage BuildAnswerRequest(BenchClient client, Guid sessionId, string phase, int slot)
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

    private static async Task<List<SseSubscription>> OpenStreamsAsync(
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

    private static async Task<ScenarioReport> CollectResultAsync(
        string ownerConnectionString,
        BenchmarkSession session,
        BenchmarkOptions options,
        DateTimeOffset questionDeadline,
        IReadOnlyList<CommandSample> commands,
        BenchmarkMeasurements measurements,
        IReadOnlyList<SessionTurnSample> coldActivation,
        IReadOnlyList<TimeSpan> coldPoolWaits,
        IReadOnlyList<SseSubscription> streams,
        CancellationToken cancellationToken)
    {
        if (options.ItemCount == 1)
        {
            await WaitForCompletionAsync(ownerConnectionString, session.Id, cancellationToken);
        }

        var accepted = await AcceptedPlayersAsync(ownerConnectionString, session.Id, cancellationToken);
        var reconciledCommands = commands.Select(command =>
            command.Outcome == CommandOutcome.Accepted && !accepted.Contains(command.PlayerId)
                ? command with { Outcome = CommandOutcome.Failed, Error = "accepted-response-without-persisted-answer" }
                : command).ToArray();
        return BenchmarkReportAssembler.Build(
            options,
            streams.Sum(stream => stream.ViewsReceived),
            questionDeadline,
            reconciledCommands,
            measurements.Samples,
            measurements.TimerLags,
            measurements.PoolWaits,
            coldActivation,
            coldPoolWaits);
    }

    private static async Task WaitForPhaseAsync(string ownerConnectionString, Guid sessionId, string phase, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(ownerConnectionString, sessionId, "select phase = @Expected from sessions where id = @SessionId", new { Expected = phase, SessionId = sessionId }, cancellationToken);
    }

    private static async Task WaitForCompletionAsync(string ownerConnectionString, Guid sessionId, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(ownerConnectionString, sessionId, "select status = 'finished' from sessions where id = @SessionId", new { SessionId = sessionId }, cancellationToken);
    }

    private static async Task WaitUntilAsync(string connectionString, Guid sessionId, string sql, object arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            var completed = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(sql, arguments, cancellationToken: timeout.Token));
            if (completed == true)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task<DateTimeOffset> GetDeadlineAsync(string connectionString, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition("select due_at from sessions where id = @SessionId", new { SessionId = sessionId }, cancellationToken: cancellationToken));
    }

    private static async Task<HashSet<Guid>> AcceptedPlayersAsync(string connectionString, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var players = await connection.QueryAsync<Guid>(new CommandDefinition("select player_id from session_answers where session_id = @SessionId and slot = 0 and answer is not null", new { SessionId = sessionId }, cancellationToken: cancellationToken));
        return players.ToHashSet();
    }

    private static BenchmarkRunEnvironment DescribeEnvironment(string administrationConnection, string? postgresVersion)
    {
        var builder = new NpgsqlConnectionStringBuilder(administrationConnection);
        var host = builder.Host;
        var local = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "::1";
        var cpu = RuntimeInformation.ProcessArchitecture.ToString();
        try
        {
            cpu = File.ReadLines("/proc/cpuinfo")
                .Select(line => line.Split(':', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0].Trim().Equals("model name", StringComparison.OrdinalIgnoreCase))?[1].Trim()
                ?? cpu;
        }
        catch (IOException)
        {
            // /proc is Linux-specific; the portable architecture is still useful evidence.
        }

        return new BenchmarkRunEnvironment(
            Environment.MachineName,
            cpu,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.OSDescription.Trim(),
            Environment.Version.ToString(),
            postgresVersion ?? "unavailable",
            $"{(local ? "local" : "remote")} PostgreSQL at {host}:{builder.Port}");
    }

    private static string SourceRevision()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, UseShellExecute = false });
            return process is not null && process.WaitForExit(5_000) && process.ExitCode == 0
                ? process.StandardOutput.ReadToEnd().Trim()
                : "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string CommandLine(BenchmarkOptions options)
        => options.Matrix
            ? "dotnet run --project platform/bench/Quesshi.Bench.Live -- --matrix --allow-disposable-postgres"
            : $"dotnet run --project platform/bench/Quesshi.Bench.Live -- --players {options.Players} --scenario {options.Scenario.CliName()}{(options.DisposablePostgreSql ? " --allow-disposable-postgres" : "")}";

    private sealed record GuestIdentity(Guid Id);
    private sealed record ScenarioOutcome(ScenarioReport Report, string PostgreSqlVersion);

    private sealed class BenchClient(HttpClient http, string tenantHost, string cookie, string antiforgeryToken, Guid playerId) : IDisposable
    {
        public HttpClient Http { get; } = http;
        public string TenantHost { get; } = tenantHost;
        public string Cookie { get; } = cookie;
        public string AntiforgeryToken { get; } = antiforgeryToken;
        public Guid PlayerId { get; } = playerId;
        public void Dispose() => Http.Dispose();
    }

    private sealed class BenchmarkMeasurements : ISessionTurnObserver, IDatabasePoolObserver
    {
        private readonly ConcurrentQueue<SessionTurnSample> _samples = [];
        private readonly ConcurrentQueue<TimeSpan> _timerLags = [];
        private readonly ConcurrentQueue<TimeSpan> _poolWaits = [];

        public IReadOnlyCollection<SessionTurnSample> Samples => _samples.ToArray();
        public IReadOnlyCollection<TimeSpan> TimerLags => _timerLags.ToArray();
        public IReadOnlyCollection<TimeSpan> PoolWaits => _poolWaits.ToArray();
        public void Record(SessionTurnSample sample) => _samples.Enqueue(sample);
        public void RecordTimerLag(TimeSpan lag) => _timerLags.Enqueue(lag);
        public void RecordApplicationPoolWait(TimeSpan elapsed) => _poolWaits.Enqueue(elapsed);
        public void Reset()
        {
            while (_samples.TryDequeue(out _)) { }
            while (_timerLags.TryDequeue(out _)) { }
            while (_poolWaits.TryDequeue(out _)) { }
        }
    }

    private sealed class SseSubscription : IAsyncDisposable
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

    private sealed class BenchmarkDatabase(string databaseName, string adminConnectionString, string ownerConnectionString, string appConnectionString, string systemConnectionString, string tenantHost, string postgreSqlVersion) : IAsyncDisposable
    {
        public string OwnerConnectionString { get; } = ownerConnectionString;
        public string AppConnectionString { get; } = appConnectionString;
        public string SystemConnectionString { get; } = systemConnectionString;
        public string TenantHost { get; } = tenantHost;
        public string PostgreSqlVersion { get; } = postgreSqlVersion;

        public static async Task<BenchmarkDatabase> CreateAsync(string administrationConnection, CancellationToken cancellationToken)
        {
            var databaseName = $"quesshi_bench_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnectionStringBuilder(administrationConnection) { Pooling = false };
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition($"create database {databaseName}", cancellationToken: cancellationToken));
            }

            var databaseConnection = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = databaseName };
            string version;
            await using (var connection = new NpgsqlConnection(databaseConnection.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                version = await connection.ExecuteScalarAsync<string>(new CommandDefinition("show server_version", cancellationToken: cancellationToken)) ?? "unknown";
                var bootstrap = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "bootstrap-roles.sql"), cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(bootstrap, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition($"alter role {OwnerUser} password '{RolePassword}'; alter role {AppUser} password '{RolePassword}'; alter role {SystemUser} password '{RolePassword}';", cancellationToken: cancellationToken));
            }

            static string ForRole(NpgsqlConnectionStringBuilder source, string user)
            {
                source.Username = user;
                source.Password = RolePassword;
                source.Pooling = true;
                source.MaxPoolSize = 40;
                return source.ConnectionString;
            }

            var owner = ForRole(new NpgsqlConnectionStringBuilder(databaseConnection.ConnectionString), OwnerUser);
            var app = ForRole(new NpgsqlConnectionStringBuilder(databaseConnection.ConnectionString), AppUser);
            var system = ForRole(new NpgsqlConnectionStringBuilder(databaseConnection.ConnectionString), SystemUser);
            await Quesshi.Server.Program.MigrateAsync(owner, cancellationToken);
            var tenantHost = $"bench-{Guid.NewGuid():N}.example.test";
            return new BenchmarkDatabase(databaseName, admin.ConnectionString, owner, app, system, tenantHost, version);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = new NpgsqlConnection(adminConnectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync($"select pg_terminate_backend(pid) from pg_stat_activity where datname = '{databaseName}' and pid <> pg_backend_pid();");
                await connection.ExecuteAsync($"drop database if exists {databaseName};");
            }
            catch
            {
                // A retained database is useful evidence for diagnosing a failed benchmark setup.
            }
        }
    }

    private readonly record struct HostPorts(int Silo, int Gateway)
    {
        public static HostPorts Allocate() => new(AllocatePort(), AllocatePort());
        public static int AllocateHttp() => AllocatePort();
        private static int AllocatePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}

internal static class LiveScenarioNames
{
    public static string CliName(this LiveScenario scenario) => scenario switch
    {
        LiveScenario.Burst => "burst",
        LiveScenario.Spread => "spread",
        LiveScenario.BurstStreams => "burst-streams",
        LiveScenario.Cold => "cold",
        LiveScenario.Pool10 => "pool10",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };
}
