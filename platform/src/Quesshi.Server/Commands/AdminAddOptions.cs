namespace Quesshi.Server.Commands;

internal sealed record AdminAddOptions(string Username, string Email, string Password, string? TenantSlug, bool IsPlatform)
{
    public static AdminAddOptions Parse(IReadOnlyList<string> args)
    {
        string? tenantSlug = null;
        var isPlatform = false;
        var positionals = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--tenant":
                    if (++index >= args.Count) throw new CommandException("--tenant requires a slug.");
                    tenantSlug = tenantSlug is null
                        ? Required("--tenant", args[index])
                        : throw new CommandException("--tenant may be supplied only once.");
                    break;
                case "--platform":
                    if (isPlatform) throw new CommandException("--platform may be supplied only once.");
                    isPlatform = true;
                    break;
                case var option when option.StartsWith("-", StringComparison.Ordinal):
                    throw new CommandException("Unknown admin add option.");
                default:
                    positionals.Add(args[index]);
                    break;
            }
        }

        if (positionals.Count != 3)
        {
            throw new CommandException("admin add expects <username> <email> <password>.");
        }

        return new AdminAddOptions(
            Required("<username>", positionals[0]),
            Required("<email>", positionals[1]),
            Required("<password>", positionals[2]),
            tenantSlug,
            isPlatform);
    }

    private static string Required(string name, string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new CommandException($"{name} is required.") : value.Trim();
}
