using Npgsql;
using Xunit;

namespace Quesshi.Testing;

public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AdminConnectionStringEnvironmentVariable = "QUESSHI_TEST_POSTGRES";
    public const string RolePassword = "quesshi-test-role-password";
    private const long BootstrapLockKey = 869_327_625_914_302_761;

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
        try
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await BootstrapRolesAsync(connection);
            await using (var command = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(DatabaseName)}", connection))
            {
                await command.ExecuteNonQueryAsync();
            }
            await using (var grant = new NpgsqlCommand($"GRANT CREATE ON DATABASE {QuoteIdentifier(DatabaseName)} TO quesshi_owner", connection))
            {
                await grant.ExecuteNonQueryAsync();
            }

            ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
            {
                Database = DatabaseName
            }.ConnectionString;
            await using var databaseConnection = new NpgsqlConnection(ConnectionString);
            await databaseConnection.OpenAsync();
            await using var schemaGrant = new NpgsqlCommand("grant usage, create on schema public to quesshi_owner", databaseConnection);
            await schemaGrant.ExecuteNonQueryAsync();
        }
        catch
        {
            var databaseName = DatabaseName;
            DatabaseName = null;
            ConnectionString = null;
            try
            {
                await DropDatabaseAsync(databaseName);
            }
            catch
            {
                // Preserve the initialization failure when cleanup cannot reach PostgreSQL.
            }

            throw;
        }
    }

    public string ConnectionStringForRole(string role) => new NpgsqlConnectionStringBuilder(ConnectionString)
    {
        Username = role,
        Password = RolePassword
    }.ConnectionString;

    public async Task DisposeAsync()
    {
        var databaseName = DatabaseName;
        DatabaseName = null;
        ConnectionString = null;
        if (string.IsNullOrWhiteSpace(_adminConnectionString) || string.IsNullOrWhiteSpace(databaseName)) return;

        await DropDatabaseAsync(databaseName);
    }

    private async Task DropDatabaseAsync(string? databaseName)
    {
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

    private static async Task BootstrapRolesAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", BootstrapLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        await ExecuteBootstrapAsync(connection, transaction);
        await using (var rolePasswords = new NpgsqlCommand($"alter role quesshi_owner password '{RolePassword}'; alter role quesshi_app password '{RolePassword}'; alter role quesshi_system password '{RolePassword}';", connection, transaction))
        {
            await rolePasswords.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task ExecuteBootstrapAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deploy", "bootstrap-roles.sql");
        var sql = await File.ReadAllTextAsync(path);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
