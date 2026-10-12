using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Quesshi.Infrastructure.Migrations;

public sealed record MigrationScript(string Name, string Owner, ReadOnlyMemory<byte> Contents)
{
    public string Checksum => Convert.ToHexString(SHA256.HashData(Contents.Span)).ToLowerInvariant();
}

public sealed class MigrationRunner(IEnumerable<MigrationScript> scripts)
{
    private const long MigrationLock = 638_002_989_883_045_713;
    private static readonly Regex FeatureResource = new(
        "(?:^|\\.)Features\\.(?<owner>[a-z0-9][a-z0-9_-]*)\\.Migrations\\.",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IReadOnlyList<MigrationScript> _scripts = scripts.OrderBy(script => script.Name, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<MigrationScript> Discover(params Assembly[] assemblies)
    {
        var migrations = new List<MigrationScript>();
        foreach (var assembly in assemblies)
        {
            foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)))
            {
                var fileName = resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
                var applicationMarker = ".Application.Migrations.";
                var feature = FeatureResource.Match(resourceName);
                var owner = resourceName.Contains(applicationMarker, StringComparison.Ordinal) ? "core" : feature.Groups["owner"].Value;
                if (string.IsNullOrEmpty(owner))
                {
                    throw new InvalidOperationException($"Migration resource '{resourceName}' must live in Application/Migrations or Features/<owner>/Migrations.");
                }

                if (!HasValidName(fileName, owner))
                {
                    throw new InvalidOperationException($"Migration resource '{resourceName}' must be named YYYYMMDDTHHMM_{owner}_name.sql.");
                }

                using var stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Could not open migration resource '{resourceName}'.");
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                migrations.Add(new MigrationScript(fileName, owner, bytes.ToArray()));
            }
        }

        return migrations.OrderBy(migration => migration.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task RunAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateScripts();

        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, "select pg_advisory_lock(@lock)", cancellationToken, ("lock", MigrationLock));
            try
            {
                var applied = await ReadAppliedAsync(connection, cancellationToken);
                ValidateApplied(applied);
                foreach (var script in _scripts.Where(script => !applied.ContainsKey(script.Name)))
                {
                    await ApplyAsync(connection, script, cancellationToken);
                }
            }
            finally
            {
                await ExecuteAsync(connection, "select pg_advisory_unlock(@lock)", cancellationToken, ("lock", MigrationLock));
            }
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private void ValidateScripts()
    {
        var duplicates = _scripts.GroupBy(script => script.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null) throw new InvalidOperationException($"Migration '{duplicates.Key}' was discovered more than once.");
        foreach (var script in _scripts)
        {
            if (!HasValidName(script.Name, script.Owner))
            {
                throw new InvalidOperationException($"Migration '{script.Name}' must be named YYYYMMDDTHHMM_{script.Owner}_name.sql.");
            }
        }
    }

    private static bool HasValidName(string name, string owner) => Regex.IsMatch(
        name,
        $"^[0-9]{{8}}T[0-9]{{4}}_{Regex.Escape(owner)}_[a-z0-9][a-z0-9_-]*\\.sql$",
        RegexOptions.CultureInvariant);

    private async Task<Dictionary<string, string>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("select to_regclass('public.schema_migrations') is not null", connection);
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false)) return new(StringComparer.Ordinal);

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("select name, checksum from schema_migrations order by name", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) applied.Add(reader.GetString(0), reader.GetString(1));
        return applied;
    }

    private void ValidateApplied(IReadOnlyDictionary<string, string> applied)
    {
        foreach (var (name, checksum) in applied)
        {
            var script = _scripts.SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (script is null) throw new InvalidOperationException($"Applied migration '{name}' is missing from the embedded migration stream.");
            if (!string.Equals(script.Checksum, checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Migration '{name}' has changed since it was applied.");
            }
        }

        var latest = applied.Keys.Max(StringComparer.Ordinal);
        if (latest is null) return;
        var earlier = _scripts.FirstOrDefault(script => !applied.ContainsKey(script.Name) && string.CompareOrdinal(script.Name, latest) < 0);
        if (earlier is not null)
        {
            throw new InvalidOperationException($"New migration '{earlier.Name}' sorts before latest applied migration '{latest}'.");
        }
    }

    private static async Task ApplyAsync(NpgsqlConnection connection, MigrationScript script, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migration = new NpgsqlCommand(Encoding.UTF8.GetString(script.Contents.Span), connection, transaction))
        {
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ledger = new NpgsqlCommand("insert into schema_migrations (name, owner, checksum) values (@name, @owner, @checksum)", connection, transaction))
        {
            ledger.Parameters.AddWithValue("name", script.Name);
            ledger.Parameters.AddWithValue("owner", script.Owner);
            ledger.Parameters.AddWithValue("checksum", script.Checksum);
            await ledger.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
