using System.Reflection;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Identity;

/// <summary>Registers external sign-in plugins and their tenant capability definitions.</summary>
internal sealed class SignInRegistry(CapabilityRegistry capabilities)
{
    private readonly Dictionary<string, RegisteredSignIn> _signIns = new(StringComparer.Ordinal);

    public IReadOnlyCollection<RegisteredSignIn> SignIns => _signIns.Values;

    public void Register(ISignIn signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentException.ThrowIfNullOrWhiteSpace(signIn.Id);
        ArgumentNullException.ThrowIfNull(signIn.Settings);

        var id = signIn.Id.Trim();
        if (_signIns.ContainsKey(id))
        {
            throw new InvalidOperationException($"Sign-in method '{id}' is already registered.");
        }

        var errors = signIn.Settings.Validate(signIn.Settings.DefaultSettings);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Sign-in method '{id}' has invalid default settings: {string.Join(" ", errors)}");
        }

        capabilities.Register(CapabilityType.Signin, id, signIn.Settings);
        _signIns.Add(id, new RegisteredSignIn(id, signIn));
    }

    public RegisteredSignIn? TryGet(string id) => _signIns.GetValueOrDefault(id);

    public static IReadOnlyList<ISignIn> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ISignIn).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => Activator.CreateInstance(type) as ISignIn
                ?? throw new InvalidOperationException($"Sign-in method '{type.FullName}' requires a public parameterless constructor."))
            .ToArray();
    }

    public static SignInRegistry Create(CapabilityRegistry capabilities, IEnumerable<ISignIn> signIns)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(signIns);
        var registry = new SignInRegistry(capabilities);
        foreach (var signIn in signIns) registry.Register(signIn);
        return registry;
    }

    internal sealed record RegisteredSignIn(string Id, ISignIn SignIn);
}
