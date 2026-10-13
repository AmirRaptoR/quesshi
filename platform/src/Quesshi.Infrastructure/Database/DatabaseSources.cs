using Npgsql;

namespace Quesshi.Infrastructure.Database;

/// <summary>Owns the two runtime pools. Migration credentials are deliberately not accepted here.</summary>
public sealed class DatabaseSources : IAsyncDisposable
{
    public static DatabaseSources FromEnvironment() => new(
        RequiredConnectionString("ConnectionStrings__App"),
        RequiredConnectionString("ConnectionStrings__System"));

    public DatabaseSources(string appConnectionString, string systemConnectionString)
    {
        App = Build(appConnectionString, "quesshi_app", 40);
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
