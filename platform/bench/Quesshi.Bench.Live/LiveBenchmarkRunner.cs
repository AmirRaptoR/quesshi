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
            ? (from players in options.MatrixCounts
               from scenario in Enum.GetValues<LiveScenario>()
               from repetition in Enumerable.Range(1, options.Repetitions)
               select (Options: options with { Players = players, Scenario = scenario, Matrix = false }, Repetition: repetition)).ToArray()
            : Enumerable.Range(1, options.Repetitions).Select(repetition => (Options: options, Repetition: repetition)).ToArray();

        var reports = new List<ScenarioReport>();
        string? postgresVersion = null;
        foreach (var (run, repetition) in runs)
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
            reports.Add(outcome.Report with { Repetition = repetition });
            postgresVersion ??= outcome.PostgreSqlVersion;
        }

        return new BenchmarkReport(
            SourceRevision(),
            DateTimeOffset.UtcNow,
            CommandLine(options),
            DescribeEnvironment(administrationConnection, postgresVersion),
            reports)
        {
            Selection = options.Matrix ? BudgetClassifier.Select(reports) : null,
        };
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
        var priorJobs = Environment.GetEnvironmentVariable("Jobs__Enabled");
        Environment.SetEnvironmentVariable("Jobs__Enabled", "false");

        DatabaseSources? firstSources = null;
        DatabaseSources? secondSources = null;
        WebApplication? first = null;
        WebApplication? second = null;
        LoadWorkerProcess? worker = null;
        try
        {
            var tenant = await BenchmarkFixture.SeedTenantAsync(database.OwnerConnectionString, database.TenantHost, cancellationToken);

            firstSources = new DatabaseSources(database.AppConnectionString, database.SystemConnectionString, options.ApplicationPoolSize, measurements);
            // Both silos share one application data source so the pool-10 scenario is
            // genuinely capped at ten connections across the complete live path.
            secondSources = firstSources;
            first = await StartHostAsync(firstSources, firstPorts, formats, kinds, measurements, cancellationToken);
            second = await StartHostAsync(secondSources, secondPorts, formats, kinds, measurements, cancellationToken);

            worker = LoadWorkerProcess.Start();
            var setup = await worker.SendAsync(new WorkerRequest(
                "setup",
                Endpoints: [first.Urls.Single(), second.Urls.Single()],
                TenantHost: database.TenantHost,
                Count: options.Players));
            var playerIds = setup.PlayerIds!;
            await WarmUpAsync(database.OwnerConnectionString, tenant, options, worker, playerIds, cancellationToken);
            measurements.Reset();
            var session = await BenchmarkFixture.SeedSessionAsync(database.OwnerConnectionString, tenant, options, playerIds, cancellationToken);

            // The first real client command activates the grain and begins the shared
            // countdown. Open the stream cohort during that countdown so setup cannot
            // consume the question window that is under measurement.
            await worker.SendAsync(new WorkerRequest("first", Session: session.Id));
            if (options.StreamCount > 0)
            {
                await worker.SendAsync(new WorkerRequest("streams", Count: options.StreamCount, Session: session.Id));
            }
            await WaitForPhaseAsync(database.OwnerConnectionString, session.Id, "1", cancellationToken);

            // Login, seed, countdown, and initial view traffic are not benchmark samples. The
            // first command remains a distinct activation measurement for the cold scenario.
            var coldActivation = options.Scenario == LiveScenario.Cold
                ? measurements.Samples.Where(sample => sample.Kind == SessionTurnKind.ColdActivation).ToArray()
                : [];
            var coldPoolWaits = options.Scenario == LiveScenario.Cold ? measurements.PoolWaits.ToArray() : [];
            measurements.Reset();
            // Start the window from a clean heap so a collection of the setup's garbage is not charged to the first answers.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var gcBefore = (Gen0: GC.CollectionCount(0), Gen1: GC.CollectionCount(1), Gen2: GC.CollectionCount(2), Pause: GC.GetTotalPauseDuration());
            var deadline = await GetDeadlineAsync(database.OwnerConnectionString, session.Id, cancellationToken);
            var commands = (await worker.SendAsync(new WorkerRequest("burst", Session: session.Id, Scenario: options.Scenario, Deadline: deadline))).Samples!;
            var views = (await worker.SendAsync(new WorkerRequest("views"))).Views;
            var gc = new GcActivity(
                GC.CollectionCount(0) - gcBefore.Gen0,
                GC.CollectionCount(1) - gcBefore.Gen1,
                GC.CollectionCount(2) - gcBefore.Gen2,
                (GC.GetTotalPauseDuration() - gcBefore.Pause).TotalMilliseconds);
            var final = await CollectResultAsync(database.OwnerConnectionString, session, options, deadline, commands, measurements, coldActivation, coldPoolWaits, views, gc, cancellationToken);
            return new ScenarioOutcome(final, database.PostgreSqlVersion);
        }
        finally
        {
            if (worker is not null)
            {
                await worker.DisposeAsync();
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

    /// <summary>
    /// Plays one complete throw-away session through the same silos so JIT compilation, connection setup and statement
    /// preparation are not charged to the measured session. The measurements are reset afterwards.
    /// </summary>
    private static async Task WarmUpAsync(
        string ownerConnectionString,
        BenchmarkTenant tenant,
        BenchmarkOptions options,
        LoadWorkerProcess worker,
        IReadOnlyList<Guid> playerIds,
        CancellationToken cancellationToken)
    {
        var warm = playerIds.Take(Math.Min(playerIds.Count, WarmUpPlayers)).ToArray();
        var warmOptions = options with { Players = warm.Length, Scenario = LiveScenario.Burst };
        var session = await BenchmarkFixture.SeedSessionAsync(ownerConnectionString, tenant, warmOptions, warm, cancellationToken);
        await worker.SendAsync(new WorkerRequest("first", Session: session.Id));
        await WaitForPhaseAsync(ownerConnectionString, session.Id, "1", cancellationToken);
        await worker.SendAsync(new WorkerRequest("answer-all", Count: warm.Length, Session: session.Id));
        await WaitForCompletionAsync(ownerConnectionString, session.Id, cancellationToken);
    }

    private const int WarmUpPlayers = 25;

    private static async Task<WebApplication> StartHostAsync(
        DatabaseSources sources,
        HostPorts ports,
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
        app.Urls.Add("http://127.0.0.1:0");
        await app.Services.GetRequiredService<PostgresClock>().StartAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        return app;
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
        int viewsReceived,
        GcActivity gc,
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
            viewsReceived,
            questionDeadline,
            reconciledCommands,
            measurements.Samples,
            measurements.TimerLags,
            measurements.PoolWaits,
            coldActivation,
            coldPoolWaits,
            measurements.AnswerCommits,
            gc);
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

            try { await Task.Delay(50, timeout.Token); } catch (OperationCanceledException) { throw new TimeoutException($"Timed out waiting for: {sql}"); }
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
            ? "dotnet run --project platform/bench/Quesshi.Bench.Live -- --matrix"
              + (options.Counts is null ? "" : $" --counts {string.Join(",", options.Counts)}")
              + (options.Repetitions == 1 ? "" : $" --repetitions {options.Repetitions}")
              + " --allow-disposable-postgres"
            : $"dotnet run --project platform/bench/Quesshi.Bench.Live -- --players {options.Players} --scenario {options.Scenario.CliName()}{(options.Repetitions == 1 ? "" : $" --repetitions {options.Repetitions}")}{(options.DisposablePostgreSql ? " --allow-disposable-postgres" : "")}";

    private sealed record ScenarioOutcome(ScenarioReport Report, string PostgreSqlVersion);

    private sealed class BenchmarkMeasurements : ISessionTurnObserver, IDatabasePoolObserver
    {
        private readonly ConcurrentQueue<SessionTurnSample> _samples = [];
        private readonly ConcurrentQueue<TimeSpan> _timerLags = [];
        private readonly ConcurrentQueue<TimeSpan> _poolWaits = [];
        private readonly ConcurrentQueue<TimeSpan> _answerCommits = [];

        public IReadOnlyCollection<SessionTurnSample> Samples => _samples.ToArray();
        public IReadOnlyCollection<TimeSpan> TimerLags => _timerLags.ToArray();
        public IReadOnlyCollection<TimeSpan> PoolWaits => _poolWaits.ToArray();
        public IReadOnlyCollection<TimeSpan> AnswerCommits => _answerCommits.ToArray();
        public void RecordAnswerCommit(TimeSpan elapsed) => _answerCommits.Enqueue(elapsed);
        public void Record(SessionTurnSample sample) => _samples.Enqueue(sample);
        public void RecordTimerLag(TimeSpan lag) => _timerLags.Enqueue(lag);
        public void RecordApplicationPoolWait(TimeSpan elapsed) => _poolWaits.Enqueue(elapsed);
        public void Reset()
        {
            while (_samples.TryDequeue(out _)) { }
            while (_timerLags.TryDequeue(out _)) { }
            while (_poolWaits.TryDequeue(out _)) { }
            while (_answerCommits.TryDequeue(out _)) { }
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
        // Ports are reserved with an exclusive per-port file lock so concurrent benchmark or test
        // processes cannot be handed the same OS-assigned port; the locks last until process exit.
        private static readonly List<FileStream> Reservations = [];

        public static HostPorts Allocate() => new(AllocatePort(), AllocatePort());

        private static int AllocatePort()
        {
            while (true)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                try
                {
                    var reservation = new FileStream(
                        Path.Combine(Path.GetTempPath(), $"quesshi-test-port-{port}.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.DeleteOnClose);
                    lock (Reservations) Reservations.Add(reservation);
                    return port;
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

public static class LiveScenarioNames
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
