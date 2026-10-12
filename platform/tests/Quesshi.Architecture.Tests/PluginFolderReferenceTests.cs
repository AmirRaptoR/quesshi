using System.Reflection;
using System.Xml.Linq;

namespace Quesshi.Architecture.Tests;

public sealed class PluginFolderReferenceTests
{
    [Fact]
    public void Empty_production_plugin_assemblies_have_no_sibling_plugin_references()
    {
        var assemblies = new[]
        {
            typeof(Quesshi.Kinds.KindsMarker).Assembly,
            typeof(Quesshi.Formats.FormatsMarker).Assembly,
            typeof(Quesshi.Features.FeaturesMarker).Assembly
        };

        Assert.All(assemblies, assembly => Assert.Empty(PluginFolderReferences.Find(assembly)));
    }

    [Fact]
    public void Deliberately_violating_compiled_fixture_is_detected()
    {
        var violation = Assert.Single(PluginFolderReferences.Find(typeof(Quesshi.Kinds.First.DependsOnSibling).Assembly));

        Assert.Equal("Quesshi.Kinds.First", violation.SourceFolder);
        Assert.Equal("Quesshi.Kinds.Second", violation.TargetFolder);
    }

    [Theory]
    [InlineData("Quesshi.Kinds", "Quesshi.Shared")]
    [InlineData("Quesshi.Formats", "Quesshi.Engine.Contracts", "Quesshi.Domain", "Quesshi.Shared")]
    [InlineData("Quesshi.Features", "Quesshi.Plugin.Api")]
    public void Plugin_project_references_match_the_platform_contract(string project, params string[] expected)
    {
        var document = XDocument.Load(Path.Combine(PlatformDirectory.Find(), "src", project, $"{project}.csproj"));
        var actual = document.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
            .Order()
            .ToArray();

        Assert.Equal(expected.Order(), actual);
    }

    [Fact]
    public void Web_project_has_no_transitive_path_to_server_or_data_projects()
    {
        var references = TransitiveProjectReferences("Quesshi.Web");

        Assert.DoesNotContain("Quesshi.Application", references);
        Assert.DoesNotContain("Quesshi.Infrastructure", references);
        Assert.DoesNotContain("Quesshi.Grains", references);
        Assert.DoesNotContain("Quesshi.Server", references);
    }

    private static IReadOnlySet<string> TransitiveProjectReferences(string project)
    {
        var root = PlatformDirectory.Find();
        var references = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([project]);

        while (pending.TryDequeue(out var current))
        {
            var document = XDocument.Load(Path.Combine(root, "src", current, $"{current}.csproj"));
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var name = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value);
                if (references.Add(name)) pending.Enqueue(name);
            }
        }

        return references;
    }
}

public sealed record PluginFolderReference(string SourceFolder, string TargetFolder, string SourceType, string TargetType);

public static class PluginFolderReferences
{
    private static readonly string[] Roots = ["Quesshi.Kinds", "Quesshi.Formats", "Quesshi.Features"];

    public static IReadOnlyList<PluginFolderReference> Find(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(source => ReferencedTypes(source).Select(target => Create(source, target)))
            .Where(violation => violation is not null)
            .Cast<PluginFolderReference>()
            .Distinct()
            .ToArray();

    private static PluginFolderReference? Create(Type source, Type target)
    {
        var sourceFolder = Folder(source.Namespace);
        var targetFolder = Folder(target.Namespace);
        return sourceFolder is { } sourcePlugin && targetFolder is { } targetPlugin && sourcePlugin.Root == targetPlugin.Root && sourcePlugin.Value != targetPlugin.Value
            ? new PluginFolderReference(sourcePlugin.Value, targetPlugin.Value, source.FullName!, target.FullName!)
            : null;
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        if (type.BaseType is not null) yield return type.BaseType;
        foreach (var implemented in type.GetInterfaces()) yield return implemented;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) yield return field.FieldType;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) yield return property.PropertyType;
        foreach (var @event in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) yield return @event.EventHandlerType!;
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
        }
    }

    private static (string Root, string Value)? Folder(string? @namespace)
    {
        var root = Roots.SingleOrDefault(candidate => @namespace?.StartsWith(candidate + ".", StringComparison.Ordinal) == true);
        if (root is null) return null;
        var segments = @namespace!.Split('.');
        return segments.Length < 3 ? null : (root, string.Join('.', segments.Take(3)));
    }
}

internal static class PlatformDirectory
{
    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quesshi.Platform.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the platform solution.");
    }
}
