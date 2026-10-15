using Npgsql;
using Quesshi.Infrastructure.Clock;

namespace Quesshi.Infrastructure.Database;

/// <summary>Owns the two runtime pools. Migration credentials are deliberately not accepted here.</summary>
public sealed class DatabaseSources : IAsyncDisposable
{
    public static DatabaseSources FromEnvironment() => new(
        RequiredConnectionString("ConnectionStrings__App"),
        RequiredConnectionString("ConnectionStrings__System"));

    private readonly IDatabasePoolObserver _poolObserver;

    public DatabaseSources(
        string appConnectionString,
        string systemConnectionString,
        int applicationPoolSize = 40,
        IDatabasePoolObserver? poolObserver = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(applicationPoolSize, 1);
        _poolObserver = poolObserver ?? NullDatabasePoolObserver.Instance;
        App = Build(appConnectionString, "quesshi_app", applicationPoolSize);
        System = Build(systemConnectionString, "quesshi_system", 10);
        SystemConnectionString = new NpgsqlConnectionStringBuilder(systemConnectionString)
        {
            Username = "quesshi_system",
            MaxPoolSize = 10
        }.ConnectionString;
    }

    internal NpgsqlDataSource App { get; }

    internal NpgsqlDataSource System { get; }

    public string SystemConnectionString { get; }

    internal async Task<NpgsqlConnection> OpenApplicationConnectionAsync(CancellationToken cancellationToken = default)
    {
        var started = PoolWaitTimer.Start();
        try
        {
            return await App.OpenConnectionAsync(cancellationToken);
        }
        finally
        {
            _poolObserver.RecordApplicationPoolWait(PoolWaitTimer.Elapsed(started));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await System.DisposeAsync();
    }

    private static NpgsqlDataSource Build(string connectionString, string role, int maximumPoolSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = role,
            MaxPoolSize = maximumPoolSize
        };
        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }

    private static string RequiredConnectionString(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } connectionString
            ? connectionString
            : throw new InvalidOperationException($"{name} is required.");
}

public interface IDatabasePoolObserver
{
    void RecordApplicationPoolWait(TimeSpan wait);
}

public sealed class NullDatabasePoolObserver : IDatabasePoolObserver
{
    public static readonly NullDatabasePoolObserver Instance = new();

    private NullDatabasePoolObserver()
    {
    }

    public void RecordApplicationPoolWait(TimeSpan wait)
    {
    }
}
