using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GBT.BuildTool;
using BuildModule = GBT.BuildTool.Module;

var options = CommandLineOptions.Parse(args);

switch (options.Mode)
{
    case "manifest":
        Generate(options.SourceRoot, options.OutputDir, GetRequestedPlatform(options));
        break;
    case "cmake":
        Generate(options.SourceRoot, options.OutputDir, GetRequestedPlatform(options));
        break;
    case "generate":
        Generate(options.SourceRoot, options.OutputDir, GetRequestedPlatform(options));
        break;
    case "reflection":
        GenerateReflection(options.SourceRoot, options.OutputDir, GetRequestedPlatform(options));
        break;
    case "configure":
        Configure(options);
        break;
    case "build":
        Build(options);
        break;
    default:
        throw new InvalidOperationException($"Unknown mode '{options.Mode}'. Expected 'generate', 'configure', or 'build'.");
}

static void Configure(CommandLineOptions options)
{
    var preset = ResolveConfigurePreset(options.SourceRoot, options.Preset);
    var outputDir = Path.Combine(preset.BinaryDir, "generated", "gbt");
    var platform = GetRequestedPlatform(options);

    Generate(options.SourceRoot, outputDir, platform);
    RunProcess("cmake", [
        "--preset", options.Preset,
        $"-DGBT_OutputDir={outputDir}",
        $"-DVCPKG_MANIFEST_DIR={Path.Combine(outputDir, "vcpkg")}",
        "-DVCPKG_MANIFEST_INSTALL=ON"
    ], options.SourceRoot);
    CopyCompileCommands(options.SourceRoot, preset.BinaryDir);
}

static void Build(CommandLineOptions options)
{
    Configure(options);
    RunProcess("cmake", ["--build", "--preset", options.Preset], options.SourceRoot);
}

static void Generate(string sourceRoot, string outputDir, ModulePlatforms platform)
{
    var modules = DiscoverModules(sourceRoot, outputDir, platform);

    Directory.CreateDirectory(outputDir);
    ReflectionGenerator.Generate(sourceRoot, outputDir, modules);
    GenerateManifest(outputDir, modules);
    GenerateCMake(sourceRoot, outputDir, modules);
}

static void GenerateReflection(string sourceRoot, string outputDir, ModulePlatforms platform)
{
    var modules = DiscoverModules(sourceRoot, outputDir, platform);

    Directory.CreateDirectory(outputDir);
    ReflectionGenerator.Generate(sourceRoot, outputDir, modules);
}

static ModulePlatforms GetRequestedPlatform(CommandLineOptions options)
{
    return options.HasPlatform ? ParsePlatform(options.Platform) : InferHostPlatform();
}

static List<BuildModule> DiscoverModules(string sourceRoot, string outputDir, ModulePlatforms platform)
{
    var roots = new[]
        {
            Path.Combine(sourceRoot, "Source"),
            Path.Combine(sourceRoot, "Games"),
            Path.Combine(CommandLineOptions.CurrentToolRoot, "Source")
        }
        .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    var moduleFiles = roots
        .SelectMany(root => Directory.EnumerateFiles(root, "*.GBTModule.cs", SearchOption.AllDirectories))
        .Distinct(StringComparer.Ordinal)
        .GroupBy(path => Path.GetFileName(path).Replace(".GBTModule.cs", "", StringComparison.Ordinal), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    return System.Reflection.Assembly.GetExecutingAssembly()
        .GetTypes()
        .Where(type => !type.IsAbstract && typeof(BuildModule).IsAssignableFrom(type))
        .Select(type =>
        {
            var grapesModule = (BuildModule)Activator.CreateInstance(type)!;

            if (!moduleFiles.TryGetValue(grapesModule.Name, out var files))
            {
                throw new InvalidOperationException($"Could not find source file for module '{grapesModule.Name}'. Expected '{grapesModule.Name}.GBTModule.cs'.");
            }

            if (files.Length != 1)
            {
                throw new InvalidOperationException($"Module '{grapesModule.Name}' matches multiple source files: {string.Join(", ", files)}");
            }

            grapesModule.SetSourceFile(files[0]);
            return grapesModule;
        })
        .Where(grapesModule => grapesModule.IsEnabled(platform))
        .OrderBy(grapesModule => grapesModule.Name, StringComparer.Ordinal)
        .ToList();
}

static void GenerateManifest(string outputDir, IReadOnlyCollection<BuildModule> modules)
{
    var vcpkgDir = Path.Combine(outputDir, "vcpkg");
    Directory.CreateDirectory(vcpkgDir);

    var dependencies = NormalizeExternalDependencies(modules);

    foreach (var dependency in dependencies)
    {
        ValidateVcpkgPortName(dependency.Name);

        foreach (var feature in dependency.Features)
        {
            ValidateVcpkgFeatureName(dependency.Name, feature);
        }
    }

    var manifestDependencies = dependencies
        .Select(dependency => dependency.Features.Count == 0
            ? (object)dependency.Name
            : new
            {
                dependency.Name,
                Features = dependency.Features
            })
        .ToArray();

    var manifest = new
    {
        name = "gbt-generated",
        versionString = "0.0.0",
        dependencies = manifestDependencies
    };

    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower
    }) + Environment.NewLine;

    var manifestFile = Path.Combine(vcpkgDir, "vcpkg.json");
    WriteFileIfChanged(manifestFile, json);

    Console.WriteLine($"[GBT] generated vcpkg manifest: {manifestFile} ({string.Join(", ", dependencies)})");
}

static ExternalDependency[] NormalizeExternalDependencies(IReadOnlyCollection<BuildModule> modules)
{
    return modules
        .SelectMany(grapesModule => grapesModule.ExternalDependencies)
        .GroupBy(dependency => dependency.Name, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group =>
        {
            var dependency = new ExternalDependency(group.Key);
            dependency.Features.AddRange(group
                .SelectMany(item => item.Features)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(feature => feature, StringComparer.Ordinal));
            return dependency;
        })
        .ToArray();
}

static void GenerateCMake(string sourceRoot, string outputDir, IReadOnlyCollection<BuildModule> modules)
{
    var builder = new StringBuilder();
    builder.AppendLine("# Generated by GBT. Do not edit.");
    builder.AppendLine();

    foreach (var grapesModule in modules)
    {
        foreach (var package in grapesModule.FindPackages)
        {
            builder.Append("find_package(");
            builder.Append(CMakeEscape(package.Name));

            if (package.Components.Count > 0)
            {
                builder.Append(" COMPONENTS");
                foreach (var component in package.Components)
                {
                    builder.Append(' ');
                    builder.Append(CMakeEscape(component));
                }
            }

            if (package.Config)
            {
                builder.Append(" CONFIG");
            }

            if (package.Required)
            {
                builder.Append(" REQUIRED");
            }

            builder.AppendLine(")");
        }

        if (grapesModule.FindPackages.Count > 0)
        {
            builder.AppendLine();
        }

        builder.Append("GBT_AddModuleFromDir(");
        builder.Append(CMakeEscape(grapesModule.Name));
        builder.Append(' ');
        builder.Append(CMakeEscape(grapesModule.ModuleDirectory));
        builder.AppendLine();
        builder.Append("    KIND ");
        builder.AppendLine(CMakeEscape(ToCMakeKind(grapesModule.Kind)));
        if (!string.IsNullOrWhiteSpace(grapesModule.Namespace))
        {
            builder.Append("    NAMESPACE ");
            builder.AppendLine(CMakeEscape(grapesModule.Namespace));
        }
        AppendList(builder, "PUBLIC_LINKS", grapesModule.PublicLinks);
        AppendList(builder, "PRIVATE_LINKS", grapesModule.PrivateLinks);
        AppendList(builder, "INTERFACE_LINKS", grapesModule.InterfaceLinks);
        AppendList(builder, "PUBLIC_DEFINITIONS", grapesModule.PublicDefinitions);
        AppendList(builder, "PRIVATE_DEFINITIONS", grapesModule.PrivateDefinitions);
        AppendList(builder, "INTERFACE_DEFINITIONS", grapesModule.InterfaceDefinitions);
        AppendList(builder, "PUBLIC_INCLUDES", grapesModule.PublicIncludes);
        AppendList(builder, "PRIVATE_INCLUDES", grapesModule.PrivateIncludes);
        AppendList(builder, "INTERFACE_INCLUDES", grapesModule.InterfaceIncludes);
        AppendList(builder, "GENERATED_SOURCES", grapesModule.GeneratedSources);

        if (!string.IsNullOrWhiteSpace(grapesModule.Pch))
        {
            builder.Append("    PCH ");
            builder.AppendLine(CMakeEscape(grapesModule.Pch));
        }

        builder.AppendLine(")");
        builder.AppendLine();
    }

    var cmakeFile = Path.Combine(outputDir, "Modules.cmake");
    WriteFileIfChanged(cmakeFile, builder.ToString());

    Console.WriteLine($"[GBT] generated module CMake: {cmakeFile} ({modules.Count} modules)");
}

static void AppendList(StringBuilder builder, string name, IReadOnlyCollection<string> values)
{
    if (values.Count == 0)
    {
        return;
    }

    builder.Append("    ");
    builder.AppendLine(name);

    foreach (var value in values)
    {
        builder.Append("        ");
        builder.AppendLine(CMakeEscape(value));
    }
}

static string ToCMakeKind(ModuleKind kind)
{
    return kind switch
    {
        ModuleKind.Static => "static",
        ModuleKind.Interface => "interface",
        ModuleKind.Executable => "executable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

static ModulePlatforms ParsePlatform(string platform)
{
    return platform.ToUpperInvariant() switch
    {
        "WINDOWS" => ModulePlatforms.Windows,
        "MAC" => ModulePlatforms.Mac,
        "LINUX" => ModulePlatforms.Linux,
        "ANDROID" => ModulePlatforms.Android,
        "EMSCRIPTEN" => ModulePlatforms.Emscripten,
        "IOS" => ModulePlatforms.Ios,
        _ => throw new InvalidOperationException($"Unsupported platform '{platform}'.")
    };
}

static string CMakeEscape(string value)
{
    return "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
}

static void ValidateVcpkgPortName(string dependency)
{
    if (dependency.Length == 0 || !dependency.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
    {
        throw new InvalidOperationException($"Invalid vcpkg dependency '{dependency}'. Use plain lowercase vcpkg port names.");
    }
}

static void ValidateVcpkgFeatureName(string dependency, string feature)
{
    if (feature.Length == 0 || !feature.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
    {
        throw new InvalidOperationException($"Invalid vcpkg feature '{feature}' on dependency '{dependency}'. Use plain lowercase vcpkg feature names.");
    }
}

static void WriteFileIfChanged(string path, string content)
{
    if (File.Exists(path) && File.ReadAllText(path) == content)
    {
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}

static ModulePlatforms InferHostPlatform()
{
    if (OperatingSystem.IsWindows())
    {
        return ModulePlatforms.Windows;
    }

    if (OperatingSystem.IsMacOS())
    {
        return ModulePlatforms.Mac;
    }

    if (OperatingSystem.IsLinux())
    {
        return ModulePlatforms.Linux;
    }

    throw new InvalidOperationException("Could not infer host platform. Pass --platform explicitly.");
}

static ConfigurePreset ResolveConfigurePreset(string sourceRoot, string presetName)
{
    var presetFile = Path.Combine(sourceRoot, "CMakePresets.json");
    var root = JsonNode.Parse(File.ReadAllText(presetFile))
        ?? throw new InvalidOperationException($"Could not parse '{presetFile}'.");

    var presets = root["configurePresets"]?.AsArray()
        ?? throw new InvalidOperationException($"'{presetFile}' does not contain configurePresets.");

    var byName = presets
        .OfType<JsonObject>()
        .ToDictionary(
            preset => preset["name"]?.GetValue<string>() ?? "",
            preset => preset,
            StringComparer.Ordinal);

    if (!byName.ContainsKey(presetName))
    {
        throw new InvalidOperationException($"Configure preset '{presetName}' was not found in '{presetFile}'.");
    }

    var merged = MergePreset(presetName, byName);
    var binaryDir = merged["binaryDir"]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Configure preset '{presetName}' does not define binaryDir.");

    binaryDir = ExpandPresetValue(binaryDir, sourceRoot, presetName);

    return new ConfigurePreset(Path.GetFullPath(binaryDir));
}

static JsonObject MergePreset(string presetName, IReadOnlyDictionary<string, JsonObject> presets)
{
    var preset = presets[presetName];
    var result = new JsonObject();

    if (preset.TryGetPropertyValue("inherits", out var inheritsNode))
    {
        var inheritedNames = inheritsNode switch
        {
            JsonArray array => array.OfType<JsonValue>().Select(value => value.GetValue<string>()),
            JsonValue value => [value.GetValue<string>()],
            _ => []
        };

        foreach (var inheritedName in inheritedNames)
        {
            var inherited = MergePreset(inheritedName, presets);
            foreach (var property in inherited)
            {
                result[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    foreach (var property in preset)
    {
        if (property.Key == "inherits")
        {
            continue;
        }

        result[property.Key] = property.Value?.DeepClone();
    }

    return result;
}

static string ExpandPresetValue(string value, string sourceRoot, string presetName)
{
    return value
        .Replace("${sourceDir}", sourceRoot.Replace("\\", "/"), StringComparison.Ordinal)
        .Replace("${presetName}", presetName, StringComparison.Ordinal);
}

static void CopyCompileCommands(string sourceRoot, string binaryDir)
{
    var source = Path.Combine(binaryDir, "compile_commands.json");
    var destination = Path.Combine(sourceRoot, "compile_commands.json");

    if (!File.Exists(source))
    {
        Console.WriteLine($"[GBT] compile_commands.json was not generated at {source}");
        return;
    }

    File.Copy(source, destination, overwrite: true);
    Console.WriteLine($"[GBT] copied compile_commands.json to {destination}");
}

static void RunProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = System.Diagnostics.Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start '{fileName}'.");

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"'{fileName}' exited with code {process.ExitCode}.");
    }
}

internal sealed class CommandLineOptions
{
    public required string Mode { get; init; }
    public required string SourceRoot { get; init; }
    public required string OutputDir { get; init; }
    public required string ToolRoot { get; init; }
    public required string Platform { get; init; }
    public required string Preset { get; init; }
    public required bool HasPlatform { get; init; }
    public static string CurrentToolRoot { get; private set; } = "";

    public static CommandLineOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new InvalidOperationException("Expected arguments in '--name value' form.");
            }

            values[args[index][2..]] = args[index + 1];
        }

        var parsed = new CommandLineOptions
        {
            Mode = Required(values, "mode"),
            SourceRoot = Path.GetFullPath(Optional(values, "source-root", Directory.GetCurrentDirectory())),
            ToolRoot = Path.GetFullPath(Optional(values, "tool-root", Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."))),
            OutputDir = Path.GetFullPath(Optional(values, "output-dir", Path.Combine(Directory.GetCurrentDirectory(), "Out", Optional(values, "preset", "debug"), "Build", "generated", "gbt"))),
            Platform = Optional(values, "platform", ""),
            Preset = Optional(values, "preset", "debug"),
            HasPlatform = values.ContainsKey("platform")
        };
        CurrentToolRoot = parsed.ToolRoot;
        return parsed;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing required argument '--{name}'.");
    }

    private static string Optional(IReadOnlyDictionary<string, string> values, string name, string defaultValue)
    {
        return values.TryGetValue(name, out var value) ? value : defaultValue;
    }
}

internal sealed record ConfigurePreset(string BinaryDir);
