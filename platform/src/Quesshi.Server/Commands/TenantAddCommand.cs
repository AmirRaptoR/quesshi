using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Commands;

internal sealed class TenantAddCommand(ISystemDb database, CapabilityRegistry registry)
{
    public async Task<Guid> ExecuteAsync(TenantAddOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var tenantId = Guid.NewGuid();
        try
        {
            return await database.InTransactionAsync(async transaction =>
            {
                await transaction.ExecuteAsync("""
                    insert into tenants (id, slug, name, default_language)
                    values (@tenantId, @slug, @name, @defaultLanguage)
                    """, new { tenantId, options.Slug, options.Name, options.DefaultLanguage }, cancellationToken);

                for (var index = 0; index < options.Languages.Count; index++)
                {
                    var language = options.Languages[index];
                    await transaction.ExecuteAsync("""
                        insert into tenant_languages (tenant_id, code, name, english_name, direction, sort_order)
                        values (@tenantId, @code, @name, @englishName, @direction, @sortOrder)
                        """, new
                    {
                        tenantId,
                        language.Code,
                        language.Name,
                        language.EnglishName,
                        language.Direction,
                        sortOrder = index
                    }, cancellationToken);
                }

                await transaction.ExecuteAsync("""
                    insert into tenant_hosts (host, tenant_id, is_primary)
                    values (@host, @tenantId, true)
                    """, new { host = options.Host, tenantId }, cancellationToken);

                foreach (var definition in registry.Definitions)
                {
                    await transaction.ExecuteAsync("""
                        insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings)
                        values (@tenantId, @type, @key, false, false, @settings)
                        """, new
                    {
                        tenantId,
                        type = definition.Type.ToString().ToLowerInvariant(),
                        definition.Key,
                        settings = definition.SerializeObject(definition.DefaultSettingsObject)
                    }, cancellationToken);
                }

                return tenantId;
            }, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new CommandException("A tenant with this slug or host already exists.", exception);
        }
    }
}

internal sealed record TenantAddOptions(
    string Slug,
    string Name,
    string Host,
    string DefaultLanguage,
    IReadOnlyList<TenantLanguage> Languages)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);

    public static TenantAddOptions Parse(IReadOnlyList<string> args)
    {
        string? slug = null;
        string? name = null;
        string? host = null;
        string? defaultLanguage = null;
        var languages = new List<TenantLanguage>();
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (option is not "--language" && ++index >= args.Count)
            {
                throw new CommandException($"{option} requires a value.");
            }

            switch (option)
            {
                case "--slug": slug = SetOnce(option, slug, args[index]); break;
                case "--name": name = SetOnce(option, name, args[index]); break;
                case "--host": host = SetOnce(option, host, args[index]); break;
                case "--default-language": defaultLanguage = SetOnce(option, defaultLanguage, args[index]); break;
                case "--language":
                    if (++index >= args.Count) throw new CommandException("--language requires a value.");
                    languages.Add(TenantLanguage.Parse(args[index]));
                    break;
                default: throw new CommandException($"Unknown tenant add option: {option}.");
            }
        }

        return new TenantAddOptions(
            Required("--slug", slug),
            Required("--name", name),
            CanonicalizeHost(Required("--host", host)),
            Required("--default-language", defaultLanguage),
            languages);
    }

    public void Validate()
    {
        if (!SlugPattern.IsMatch(Slug)) throw new CommandException("--slug must use lower-case letters, digits, and hyphens.");
        if (string.IsNullOrWhiteSpace(Name)) throw new CommandException("--name is required.");
        if (Languages.Count == 0) throw new CommandException("At least one --language is required.");
        if (!Languages.Any(language => language.Code == DefaultLanguage)) throw new CommandException("--default-language must be offered with --language.");
        if (Languages.Select(language => language.Code).Distinct(StringComparer.Ordinal).Count() != Languages.Count)
        {
            throw new CommandException("Each --language code may be offered only once.");
        }
    }

    private static string SetOnce(string option, string? current, string value) =>
        current is null ? value : throw new CommandException($"{option} may be supplied only once.");

    private static string Required(string option, string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new CommandException($"{option} is required.") : value.Trim();

    private static string CanonicalizeHost(string value)
    {
        if (value.IndexOfAny(['/', '\\', ':', '?', '#', '@']) >= 0 || value.Any(char.IsWhiteSpace))
        {
            throw new CommandException("--host must be a host name without a scheme, port, or path.");
        }

        string canonical;
        try
        {
            canonical = new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new CommandException("--host must be a valid host name.", exception);
        }
        if (canonical.Length == 0 || canonical.StartsWith('.') || canonical.EndsWith('.'))
        {
            throw new CommandException("--host must be a valid host name.");
        }

        return canonical;
    }
}

internal sealed record TenantLanguage(string Code, string Name, string EnglishName, string Direction)
{
    public static TenantLanguage Parse(string value)
    {
        var fields = value.Split(':');
        if (fields.Length != 4 || fields.Any(string.IsNullOrWhiteSpace))
        {
            throw new CommandException("--language must be code:name:english-name:ltr-or-rtl.");
        }

        var direction = fields[3].Trim();
        if (direction is not "ltr" and not "rtl") throw new CommandException("Language direction must be ltr or rtl.");
        return new TenantLanguage(fields[0].Trim(), fields[1].Trim(), fields[2].Trim(), direction);
    }
}

internal sealed class CommandException(string message, Exception? innerException = null) : Exception(message, innerException);
