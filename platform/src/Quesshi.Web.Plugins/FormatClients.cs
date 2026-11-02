using System.Reflection;
using Microsoft.AspNetCore.Components;
using Quesshi.Shared;

namespace Quesshi.Web.Plugins;

/// <summary>Describes the client components for one session format.</summary>
public interface IFormatClient
{
    string Id { get; }
    Type Settings { get; }
    Type Screen { get; }
    Type Result { get; }

    /// <summary>Whether the format declares a server-side <c>MatchRule</c>, so Home offers a random opponent for it.</summary>
    bool Matchmaking => false;

    /// <summary>Whether the format has no lobby, so Home offers its <see cref="Settings"/> component to start a session instead of a plain new-session button.</summary>
    bool StartsFromSettings => false;
}

/// <summary>Registers and resolves format clients, slot contributions and client pages.</summary>
public interface IFormatClientRegistry
{
    void RegisterFormat(IFormatClient client);

    /// <summary>Resolves a registered format whether or not it is still offered for new play.</summary>
    IFormatClient? ResolveFormat(string id);

    /// <summary>The ids of every registered format.</summary>
    IReadOnlyList<string> FormatIds { get; }

    void RegisterPage(string path, Type component);
    IReadOnlyList<(string Path, Type Component)> Pages { get; }

    /// <summary>Adds a component that takes the slot's parameters through a single <c>Parameters</c> property.</summary>
    void RegisterSlot<TParameters>(ClientSlot<TParameters> slot, Type component);

    IReadOnlyList<Type> Contributions<TParameters>(ClientSlot<TParameters> slot);
}

public static class ClientSlotComponent
{
    public const string ParameterName = "Parameters";

    public static void EnsureAccepts(Type component, Type parameters)
    {
        var property = component.GetProperty(ParameterName, BindingFlags.Public | BindingFlags.Instance);
        if (!typeof(IComponent).IsAssignableFrom(component)
            || property?.GetCustomAttribute<ParameterAttribute>() is null
            || property.PropertyType != parameters)
            throw new ArgumentException(
                $"'{component.Name}' must be a component with a [Parameter] {parameters.Name} {ParameterName} property.", nameof(component));
    }
}
