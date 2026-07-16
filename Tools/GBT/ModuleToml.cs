using System.Text;

namespace GBT.BuildTool;

// Parses the deliberately small, versioned TOML schema used by GBT project and
// module manifests. Unsupported TOML constructs fail with a source location so
// the accepted language stays deterministic and independently testable.
internal static class ModuleToml
{
    private sealed class Table
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Document
    {
        public Table Root { get; } = new();
        public Dictionary<string, Table> Tables { get; } = new(StringComparer.Ordinal);
        public List<Table> Dependencies { get; } = [];
    }

    internal sealed record ProjectManifest(IReadOnlyList<string> ModuleRoots);

    public static ProjectManifest ReadProject(string path)
    {
        var document = Parse(path);
        RequireOnly(path, document.Root, "SchemaVersion");
        RequireSchemaVersion(path, document.Root);
        RequireTables(path, document, "Project");
        if (document.Dependencies.Count > 0) throw Error(path, "Project manifests cannot contain [[Dependency]] tables.");
        var project = RequireTable(path, document, "Project");
        RequireOnly(path, project, "Name", "ModuleRoots");
        _ = RequireString(path, project, "Name");
        var roots = RequireStringArray(path, project, "ModuleRoots");
        if (roots.Count == 0)
            throw Error(path, "Project.ModuleRoots must contain at least one directory.");
        return new ProjectManifest(roots);
    }

    public static Module ReadModule(string path)
    {
        var document = Parse(path);
        RequireOnly(path, document.Root, "SchemaVersion");
        RequireSchemaVersion(path, document.Root);
        RequireTables(path, document, "Module", "Links", "Definitions", "Includes");
        var moduleTable = RequireTable(path, document, "Module");
        RequireOnly(path, moduleTable, "Name", "Namespace", "Kind", "Platforms", "Pch");
        var moduleName = RequireString(path, moduleTable, "Name");
        if (moduleName.Length == 0 || !char.IsUpper(moduleName[0]) || moduleName.Any(character => !char.IsLetterOrDigit(character)))
            throw Error(path, $"Module name '{moduleName}' must be PascalCase and contain only letters and digits.");
        var expectedFileName = $"{moduleName}.GBTModule.toml";
        if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.Ordinal))
            throw Error(path, $"Module '{moduleName}' must be declared in '{expectedFileName}'.");
        var module = new Module(moduleName);
        module.SetSourceFile(path);
        module.Namespace = OptionalString(path, moduleTable, "Namespace") ?? "";
        if (module.Namespace.Length > 0 && module.Namespace.Split("::", StringSplitOptions.None)
            .Any(part => part.Length == 0 || !char.IsLetter(part[0]) || part.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
            throw Error(path, $"Namespace '{module.Namespace}' is not a valid C++ namespace path.");
        module.Kind = ParseKind(path, OptionalString(path, moduleTable, "Kind") ?? "Static");
        module.Platforms = ParsePlatforms(path, OptionalStringArray(path, moduleTable, "Platforms") ?? ["All"]);
        module.Pch = OptionalString(path, moduleTable, "Pch");

        ReadVisibilityTable(path, document, "Links", module.PublicLinks, module.PrivateLinks, module.InterfaceLinks);
        ReadVisibilityTable(path, document, "Definitions", module.PublicDefinitions, module.PrivateDefinitions, module.InterfaceDefinitions);
        ReadVisibilityTable(path, document, "Includes", module.PublicIncludes, module.PrivateIncludes, module.InterfaceIncludes);
        ResolveIncludePaths(module.PublicIncludes, module.ModuleDirectory);
        ResolveIncludePaths(module.PrivateIncludes, module.ModuleDirectory);
        ResolveIncludePaths(module.InterfaceIncludes, module.ModuleDirectory);
        var duplicateDependency = document.Dependencies.GroupBy(table => RequireString(path, table, "Name"), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDependency is not null) throw Error(path, $"Duplicate dependency '{duplicateDependency.Key}'.");
        foreach (var dependency in document.Dependencies)
            ApplyDependency(path, dependency, module);
        return module;
    }

    private static void ApplyDependency(string path, Table table, Module module)
    {
        RequireOnly(path, table, "Name", "External", "Features", "Package", "Target", "Visibility", "Config", "Required", "Components");
        var name = RequireString(path, table, "Name");
        var features = OptionalStringArray(path, table, "Features") ?? [];
        if (OptionalBool(path, table, "External") ?? true)
            module.ExternalDependencies.Add(new ExternalDependency(name, [.. features]));
        var packageName = OptionalString(path, table, "Package");
        if (packageName is not null)
        {
            var package = new PackageReference(packageName)
            {
                Config = OptionalBool(path, table, "Config") ?? true,
                Required = OptionalBool(path, table, "Required") ?? true,
            };
            package.Components.AddRange(OptionalStringArray(path, table, "Components") ?? []);
            module.FindPackages.Add(package);
        }
        var target = OptionalString(path, table, "Target");
        if (target is not null)
            SelectVisibility(path, OptionalString(path, table, "Visibility") ?? "Private", module).Add(target);
    }

    private static List<string> SelectVisibility(string path, string visibility, Module module) => visibility switch
    {
        "Public" => module.PublicLinks,
        "Private" => module.PrivateLinks,
        "Interface" => module.InterfaceLinks,
        _ => throw Error(path, $"Unknown dependency visibility '{visibility}'. Expected Public, Private, or Interface.")
    };

    private static void ResolveIncludePaths(List<string> paths, string moduleDirectory)
    {
        for (var index = 0; index < paths.Count; index++)
            paths[index] = Path.IsPathRooted(paths[index]) ? paths[index] : Path.GetFullPath(Path.Combine(moduleDirectory, paths[index]));
    }

    private static void ReadVisibilityTable(string path, Document document, string name,
        List<string> publicValues, List<string> privateValues, List<string> interfaceValues)
    {
        if (!document.Tables.TryGetValue(name, out var table))
            return;
        RequireOnly(path, table, "Public", "Private", "Interface");
        publicValues.AddRange(OptionalStringArray(path, table, "Public") ?? []);
        privateValues.AddRange(OptionalStringArray(path, table, "Private") ?? []);
        interfaceValues.AddRange(OptionalStringArray(path, table, "Interface") ?? []);
    }

    private static Document Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        var document = new Document();
        var current = document.Root;
        for (var index = 0; index < lines.Length; index++)
        {
            var text = StripComment(lines[index]).Trim();
            if (text.Length == 0)
                continue;
            if (text.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!text.EndsWith("]]", StringComparison.Ordinal) || text[2..^2].Trim() != "Dependency")
                    throw Error(path, index + 1, "Only [[Dependency]] array tables are supported in module manifests");
                current = new Table();
                document.Dependencies.Add(current);
                continue;
            }
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                if (!text.EndsWith(']'))
                    throw Error(path, index + 1, "Unterminated table header");
                var name = text[1..^1].Trim();
                if (name.Length == 0 || document.Tables.ContainsKey(name))
                    throw Error(path, index + 1, $"Invalid or duplicate table '{name}'");
                current = new Table();
                document.Tables.Add(name, current);
                continue;
            }
            while (!IsBalancedValue(text) && index + 1 < lines.Length)
                text += "\n" + StripComment(lines[++index]).Trim();
            var equals = FindUnquoted(text, '=');
            if (equals <= 0)
                throw Error(path, index + 1, "Expected a key/value assignment");
            var key = text[..equals].Trim();
            if (!IsIdentifier(key) || current.Values.ContainsKey(key))
                throw Error(path, index + 1, $"Invalid or duplicate key '{key}'");
            current.Values.Add(key, ParseValue(path, index + 1, text[(equals + 1)..].Trim()));
        }
        return document;
    }

    private static object ParseValue(string path, int line, string text)
    {
        if (text == "true") return true;
        if (text == "false") return false;
        if (text.StartsWith('"')) return ParseString(path, line, text);
        if (text.StartsWith('['))
        {
            if (!text.EndsWith(']')) throw Error(path, line, "Unterminated array");
            return SplitArray(path, line, text[1..^1]).Select(value =>
            {
                var parsed = ParseValue(path, line, value.Trim());
                return parsed as string ?? throw Error(path, line, "GBT manifest arrays currently accept strings only");
            }).ToList();
        }
        if (int.TryParse(text, out var integer)) return integer;
        throw Error(path, line, $"Unsupported TOML value '{text}'");
    }

    private static string ParseString(string path, int line, string text)
    {
        if (text.Length < 2 || text[^1] != '"') throw Error(path, line, "Unterminated string");
        var builder = new StringBuilder();
        for (var index = 1; index < text.Length - 1; index++)
        {
            if (text[index] != '\\') { builder.Append(text[index]); continue; }
            if (++index >= text.Length - 1) throw Error(path, line, "Invalid string escape");
            builder.Append(text[index] switch { '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => throw Error(path, line, $"Unsupported escape '\\{text[index]}'") });
        }
        return builder.ToString();
    }

    private static List<string> SplitArray(string path, int line, string text)
    {
        var result = new List<string>();
        var start = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
            }
            else if (value == '"') quoted = true;
            else if (value == ',') { result.Add(text[start..index]); start = index + 1; }
        }
        if (quoted) throw Error(path, line, "Unterminated string in array");
        var tail = text[start..].Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    private static string StripComment(string text)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (text[index] == '\\') escaped = true;
                else if (text[index] == '"') quoted = false;
            }
            else if (text[index] == '"') quoted = true;
            else if (text[index] == '#') return text[..index];
        }
        return text;
    }

    private static bool IsBalancedValue(string text)
    {
        var brackets = 0;
        var quoted = false;
        var escaped = false;
        foreach (var value in text)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
            }
            else if (value == '"') quoted = true;
            else if (value == '[') brackets++;
            else if (value == ']') brackets--;
        }
        return !quoted && brackets == 0;
    }

    private static int FindUnquoted(string text, char target)
    {
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"' && (index == 0 || text[index - 1] != '\\')) quoted = !quoted;
            else if (!quoted && text[index] == target) return index;
        }
        return -1;
    }

    private static bool IsIdentifier(string value) => value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
    private static Table RequireTable(string path, Document document, string name) => document.Tables.TryGetValue(name, out var table) ? table : throw Error(path, $"Missing required [{name}] table.");
    private static void RequireSchemaVersion(string path, Table root) { if (!root.Values.TryGetValue("SchemaVersion", out var value) || value is not int version || version != 1) throw Error(path, "SchemaVersion must be the integer 1."); }
    private static string RequireString(string path, Table table, string key) => OptionalString(path, table, key) ?? throw Error(path, $"Missing required string '{key}'.");
    private static string? OptionalString(string path, Table table, string key) => !table.Values.TryGetValue(key, out var value) ? null : value as string ?? throw Error(path, $"'{key}' must be a string.");
    private static bool? OptionalBool(string path, Table table, string key) => !table.Values.TryGetValue(key, out var value) ? null : value is bool boolean ? boolean : throw Error(path, $"'{key}' must be a boolean.");
    private static List<string> RequireStringArray(string path, Table table, string key) => OptionalStringArray(path, table, key) ?? throw Error(path, $"Missing required string array '{key}'.");
    private static List<string>? OptionalStringArray(string path, Table table, string key) => !table.Values.TryGetValue(key, out var value) ? null : value as List<string> ?? throw Error(path, $"'{key}' must be an array of strings.");
    private static void RequireOnly(string path, Table table, params string[] keys) { var allowed = keys.ToHashSet(StringComparer.Ordinal); var unknown = table.Values.Keys.FirstOrDefault(key => !allowed.Contains(key)); if (unknown is not null) throw Error(path, $"Unknown key '{unknown}'."); }
    private static void RequireTables(string path, Document document, params string[] names) { var allowed = names.ToHashSet(StringComparer.Ordinal); var unknown = document.Tables.Keys.FirstOrDefault(name => !allowed.Contains(name)); if (unknown is not null) throw Error(path, $"Unknown table '[{unknown}]'."); }
    private static ModuleKind ParseKind(string path, string value) => Enum.TryParse<ModuleKind>(value, ignoreCase: false, out var kind) ? kind : throw Error(path, $"Unknown module kind '{value}'.");
    private static ModulePlatforms ParsePlatforms(string path, IReadOnlyCollection<string> values) { var result = ModulePlatforms.None; foreach (var value in values) { if (!Enum.TryParse<ModulePlatforms>(value, ignoreCase: false, out var platform)) throw Error(path, $"Unknown platform '{value}'."); result |= platform; } return result; }
    private static InvalidOperationException Error(string path, string message) => new($"{path}: {message}");
    private static InvalidOperationException Error(string path, int line, string message) => new($"{path}:{line}: {message}.");
}
