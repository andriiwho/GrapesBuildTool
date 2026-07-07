namespace GBT.BuildTool;

[Flags]
public enum ModulePlatforms
{
    None = 0,
    Windows = 1 << 0,
    Mac = 1 << 1,
    Linux = 1 << 2,
    Android = 1 << 3,
    Emscripten = 1 << 4,
    Ios = 1 << 5,
    Desktop = Windows | Mac | Linux,
    All = Windows | Mac | Linux | Android | Emscripten | Ios
}

public enum ModuleKind
{
    Static,
    Interface,
    Executable
}

public sealed class PackageReference
{
    public PackageReference(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public bool Config { get; set; } = true;
    public bool Required { get; set; } = true;
    public List<string> Components { get; } = [];
}

public sealed class ExternalDependency
{
    public ExternalDependency(string name, params string[] features)
    {
        Name = name;
        Features.AddRange(features);
    }

    public string Name { get; }
    public List<string> Features { get; } = [];

    public static implicit operator ExternalDependency(string name)
    {
        return new ExternalDependency(name);
    }

    public override string ToString()
    {
        return Features.Count == 0
            ? Name
            : $"{Name}[{string.Join(",", Features)}]";
    }
}

public abstract class Module
{
    public virtual string Name => GetType().Name;
    public string SourceFile { get; private set; } = "";
    public string ModuleDirectory { get; private set; } = "";
    public ModuleKind Kind { get; protected set; } = ModuleKind.Static;
    public ModulePlatforms Platforms { get; protected set; } = ModulePlatforms.All;

    public List<ExternalDependency> ExternalDependencies { get; } = [];
    public List<PackageReference> FindPackages { get; } = [];
    public List<string> PublicLinks { get; } = [];
    public List<string> PrivateLinks { get; } = [];
    public List<string> InterfaceLinks { get; } = [];
    public List<string> PublicDefinitions { get; } = [];
    public List<string> PrivateDefinitions { get; } = [];
    public List<string> InterfaceDefinitions { get; } = [];
    public List<string> PublicIncludes { get; } = [];
    public List<string> PrivateIncludes { get; } = [];
    public List<string> InterfaceIncludes { get; } = [];
    public List<string> GeneratedSources { get; } = [];
    public string? Pch { get; protected set; }

    public bool IsEnabled(ModulePlatforms currentPlatform)
    {
        return (Platforms & currentPlatform) != 0;
    }

    internal void SetSourceFile(string sourceFile)
    {
        SourceFile = Path.GetFullPath(sourceFile);
        ModuleDirectory = Path.GetDirectoryName(SourceFile)
            ?? throw new InvalidOperationException($"Could not resolve module directory for '{sourceFile}'.");
    }
}
