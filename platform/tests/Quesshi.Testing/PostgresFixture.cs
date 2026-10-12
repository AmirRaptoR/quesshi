using Npgsql;
using Xunit;

namespace Quesshi.Testing;

public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AdminConnectionStringEnvironmentVariable = "QUESSHI_TEST_POSTGRES";

    private readonly string? _adminConnectionString;

    public PostgresFixture()
        : this(Environment.GetEnvironmentVariable(AdminConnectionStringEnvironmentVariable), IsCi())
    {
    }

    internal PostgresFixture(string? adminConnectionString, bool isCi)
    {
        _adminConnectionString = adminConnectionString;
        IsConfigured(adminConnectionString, isCi);
    }

    public string? ConnectionString { get; private set; }

    public string? DatabaseName { get; private set; }

    public static bool IsConfigured(string? adminConnectionString, bool isCi)
    {
        if (!string.IsNullOrWhiteSpace(adminConnectionString)) return true;
        if (isCi)
        {
            throw new InvalidOperationException($"{AdminConnectionStringEnvironmentVariable} must be set when CI=true.");
        }

        return false;
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            return;
        }

        DatabaseName = $"quesshi_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(DatabaseName)}", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = DatabaseName
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        var databaseName = DatabaseName;
        DatabaseName = null;
        ConnectionString = null;
        if (string.IsNullOrWhiteSpace(_adminConnectionString) || string.IsNullOrWhiteSpace(databaseName)) return;

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()", connection))
        {
            terminate.Parameters.AddWithValue("database", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)}", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static bool IsCi() => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
