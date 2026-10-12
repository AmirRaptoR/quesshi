using System.Reflection;
using System.Reflection.Emit;
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
        var violations = PluginFolderReferences.Find(typeof(Quesshi.Kinds.First.DependsOnSibling).Assembly);

        Assert.All(violations, violation =>
        {
            Assert.Equal("Quesshi.Kinds.First", violation.SourceFolder);
            Assert.Equal("Quesshi.Kinds.Second", violation.TargetFolder);
        });
        Assert.Contains(violations, violation => violation.SourceType.EndsWith("DependsOnSibling", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.SourceType.EndsWith("UsesSiblingOnlyInMethod", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.SourceType.EndsWith("UsesSiblingInsideGenericSignature", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Quesshi.Kinds", "Quesshi.Shared")]
    [InlineData("Quesshi.Formats", "Quesshi.Engine.Contracts", "Quesshi.Domain", "Quesshi.Shared")]
    [InlineData("Quesshi.Features", "Quesshi.Plugin.Api")]
    public void Plugin_project_references_match_the_platform_contract(string project, params string[] expected)
    {
        var document = XDocument.Load(Path.Combine(PlatformDirectory.Find(), "src", project, $"{project}.csproj"));
        var actual = ProjectReferences(document)
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
            foreach (var reference in ProjectReferences(document))
            {
                if (references.Add(reference)) pending.Enqueue(reference);
            }
        }

        return references;
    }

    private static IEnumerable<string> ProjectReferences(XDocument document) => document.Descendants("ProjectReference")
        .Where(reference => !string.Equals(reference.Attribute("OutputItemType")?.Value, "Analyzer", StringComparison.Ordinal))
        .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value));
}

public sealed record PluginFolderReference(string SourceFolder, string TargetFolder, string SourceType, string TargetType);

public static class PluginFolderReferences
{
    private static readonly string[] Roots = ["Quesshi.Kinds", "Quesshi.Formats", "Quesshi.Features"];
    private static readonly BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));

    public static IReadOnlyList<PluginFolderReference> Find(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(source => ReferencedTypes(source).SelectMany(Unwrap).Select(target => Create(source, target)))
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
        foreach (var genericArgument in type.GetGenericArguments())
        {
            foreach (var constraint in genericArgument.GetGenericParameterConstraints()) yield return constraint;
        }
        foreach (var field in type.GetFields(DeclaredMembers)) yield return field.FieldType;
        foreach (var property in type.GetProperties(DeclaredMembers)) yield return property.PropertyType;
        foreach (var @event in type.GetEvents(DeclaredMembers)) yield return @event.EventHandlerType!;
        foreach (var method in type.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(type.GetConstructors(DeclaredMembers)))
        {
            if (method is MethodInfo methodInfo)
            {
                yield return methodInfo.ReturnType;
                foreach (var genericArgument in methodInfo.GetGenericArguments())
                {
                    foreach (var constraint in genericArgument.GetGenericParameterConstraints()) yield return constraint;
                }
            }
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
            foreach (var referenced in MethodBodyReferencedTypes(method)) yield return referenced;
        }
    }

    private static IEnumerable<Type> MethodBodyReferencedTypes(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null) yield break;

        foreach (var local in body.LocalVariables) yield return local.LocalType;
        var instructions = body.GetILAsByteArray();
        if (instructions is null) yield break;

        for (var offset = 0; offset < instructions.Length;)
        {
            var opcode = ReadOpCode(instructions, ref offset);
            if (opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType)
            {
                var token = ReadToken(instructions, ref offset);
                foreach (var referenced in ResolveReferencedTypes(method, token)) yield return referenced;
            }
            else
            {
                SkipOperand(instructions, ref offset, opcode.OperandType);
            }
        }
    }

    private static IEnumerable<Type> ResolveReferencedTypes(MethodBase method, int token)
    {
        MemberInfo? member;
        try
        {
            var typeArguments = method.DeclaringType?.GetGenericArguments();
            var methodArguments = method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : null;
            member = method.Module.ResolveMember(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        switch (member)
        {
            case Type type:
                yield return type;
                break;
            case MethodBase referencedMethod when referencedMethod.DeclaringType is not null:
                yield return referencedMethod.DeclaringType;
                break;
            case FieldInfo referencedField when referencedField.DeclaringType is not null:
                yield return referencedField.DeclaringType;
                break;
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        var pending = new Stack<Type>([type]);
        var visited = new HashSet<Type>();
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            yield return current;
            if (current.HasElementType && current.GetElementType() is { } elementType) pending.Push(elementType);
            if (current.IsGenericType)
            {
                foreach (var argument in current.GetGenericArguments()) pending.Push(argument);
            }
        }
    }

    private static OpCode ReadOpCode(byte[] instructions, ref int offset)
    {
        ushort value = instructions[offset++];
        if (value == 0xfe) value = (ushort)(0xfe00 | instructions[offset++]);
        return OpCodes.TryGetValue(value, out var opcode) ? opcode : throw new BadImageFormatException("Unknown IL opcode.");
    }

    private static int ReadToken(byte[] instructions, ref int offset)
    {
        var token = BitConverter.ToInt32(instructions, offset);
        offset += sizeof(int);
        return token;
    }

    private static void SkipOperand(byte[] instructions, ref int offset, OperandType operandType)
    {
        var length = operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => sizeof(int) + (sizeof(int) * BitConverter.ToInt32(instructions, offset)),
            _ => throw new BadImageFormatException("Unknown IL operand type.")
        };
        offset += length;
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
