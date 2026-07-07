namespace GBT.BuildTool;

internal sealed record ReflectedMetadata(string Key, string? Value);

internal sealed class ReflectedType
{
    public required Module Module { get; init; }
    public required string SourceFile { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string DeclarationKind { get; init; }
    public required string? BaseTypeName { get; init; }
    public required int MetadataLine { get; init; }
    public required List<ReflectedMetadata> Metadata { get; init; }
    public List<ReflectedField> Fields { get; } = [];
    public List<ReflectedMethod> Methods { get; } = [];
}

internal sealed class ReflectedEnum
{
    public required Module Module { get; init; }
    public required string SourceFile { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string UnderlyingTypeName { get; init; }
    public required List<ReflectedMetadata> Metadata { get; init; }
    public List<ReflectedEnumValue> Values { get; } = [];
}

internal sealed class ReflectedEnumValue
{
    public required string Name { get; init; }
    public required List<ReflectedMetadata> Metadata { get; init; }
}

internal sealed class ReflectedField
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required List<ReflectedMetadata> Metadata { get; init; }
}

internal sealed class ReflectedMethod
{
    public required string Name { get; init; }
    public required string ReflectedName { get; init; }
    public required string ReturnTypeName { get; init; }
    public required bool IsConst { get; init; }
    public required bool IsStatic { get; init; }
    public required List<ReflectedMetadata> Metadata { get; init; }
    public List<ReflectedParameter> Parameters { get; } = [];
}

internal sealed class ReflectedParameter
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
}
