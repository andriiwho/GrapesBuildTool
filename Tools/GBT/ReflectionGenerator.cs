using System.Text;
using System.Text.RegularExpressions;

namespace GBT.BuildTool;

internal static partial class ReflectionGenerator
{
    public static void Generate(string sourceRoot, string outputDir, IReadOnlyCollection<Module> modules)
    {
        var reflectedModules = modules.Select(ScanModule).ToList();
        var reflectedTypes = reflectedModules.SelectMany(module => module.Types).ToList();
        var reflectedEnums = reflectedModules.SelectMany(module => module.Enums).ToList();
        ValidateReflectedTypeReferences(reflectedTypes, reflectedEnums);
        var reflectionDir = Path.Combine(outputDir, "reflection");
        Directory.CreateDirectory(reflectionDir);

        foreach (var module in modules)
        {
            var moduleTypes = reflectedTypes
                .Where(type => ReferenceEquals(type.Module, module))
                .ToList();
            var moduleEnums = reflectedEnums
                .Where(enumInfo => ReferenceEquals(enumInfo.Module, module))
                .ToList();
            var moduleDir = Path.Combine(reflectionDir, module.Name);
            Directory.CreateDirectory(moduleDir);

            var headerPath = Path.Combine(moduleDir, $"{module.Name}.gen.h");
            var sourcePath = Path.Combine(moduleDir, $"{module.Name}.gen.cpp");

            WriteFileIfChanged(headerPath, GenerateModuleHeader(module.Name));
            foreach (var sourceTypes in moduleTypes.GroupBy(type => type.SourceFile))
            {
                var generatedHeaderPath = Path.Combine(moduleDir, GetGeneratedHeaderIncludePath(module, sourceTypes.Key));
                WriteFileIfChanged(generatedHeaderPath, GenerateTypeMetadataHeader(module, sourceTypes.Key, sourceTypes));
            }
            WriteFileIfChanged(sourcePath, GenerateSource(sourceRoot, module.Name, moduleTypes, moduleEnums));

            if (module.Kind == ModuleKind.Interface)
            {
                module.InterfaceIncludes.Add(moduleDir);
            }
            else
            {
                module.PublicIncludes.Add(moduleDir);
                module.GeneratedSources.Add(sourcePath);
            }
        }

        WriteFileIfChanged(Path.Combine(reflectionDir, "AllReflection.gen.cpp"), GenerateAllSource(reflectedTypes, reflectedEnums));
        var reflectionModule = modules.FirstOrDefault(module => module.Name == "GBT")
            ?? modules.FirstOrDefault(module => module.Name == "Core");
        reflectionModule?.GeneratedSources.Add(Path.Combine(reflectionDir, "AllReflection.gen.cpp"));

        Console.WriteLine($"[GBT] generated reflection metadata ({reflectedTypes.Count} types, {reflectedEnums.Count} enums)");
    }

    private sealed record ReflectedModule(List<ReflectedType> Types, List<ReflectedEnum> Enums);

    private static ReflectedModule ScanModule(Module module)
    {
        var headers = Directory.EnumerateFiles(module.ModuleDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".h", StringComparison.Ordinal) || path.EndsWith(".hpp", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);

        var types = new List<ReflectedType>();
        var enums = new List<ReflectedEnum>();
        foreach (var header in headers)
        {
            var headerReflection = ScanHeader(module, header);
            types.AddRange(headerReflection.Types);
            enums.AddRange(headerReflection.Enums);
        }

        return new ReflectedModule(types, enums);
    }

    private static ReflectedModule ScanHeader(Module module, string header)
    {
        var text = StripComments(File.ReadAllText(header));
        var types = new List<ReflectedType>();
        var enums = new List<ReflectedEnum>();
        var typeIndex = 0;

        while (true)
        {
            var annotation = text.IndexOf("GBT_Type", typeIndex, StringComparison.Ordinal);
            if (annotation < 0)
            {
                break;
            }

            var argumentsStart = text.IndexOf('(', annotation);
            var argumentsEnd = FindMatching(text, argumentsStart, '(', ')');
            var afterAnnotation = argumentsEnd + 1;
            var declaration = TypeDeclarationRegex().Match(text, afterAnnotation);
            if (!declaration.Success)
            {
                throw new InvalidOperationException($"{header}: GBT_Type must be followed by a class or struct declaration.");
            }

            var bodyStart = declaration.Index + declaration.Length - 1;
            var bodyEnd = FindMatching(text, bodyStart, '{', '}');
            var body = text[(bodyStart + 1)..bodyEnd];
            var metadataIndex = body.IndexOf("GBT_TypeMetadata", StringComparison.Ordinal);
            if (metadataIndex < 0)
            {
                throw new InvalidOperationException($"{header}: reflected type '{declaration.Groups["name"].Value}' must contain GBT_TypeMetadata().");
            }

            var name = declaration.Groups["name"].Value;
            var declarationKind = declaration.Groups["kind"].Value;
            var namespaceName = FindNamespace(text[..annotation]);
            var qualifiedName = string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}::{name}";
            var baseTypes = declaration.Groups["bases"].Success ? declaration.Groups["bases"].Value.Trim() : "";
            var baseType = ParseBaseType(baseTypes);

            var type = new ReflectedType
            {
                Module = module,
                SourceFile = header,
                Name = name,
                QualifiedName = qualifiedName,
                DeclarationKind = declarationKind,
                BaseTypeName = baseType,
                MetadataLine = CountLines(text, bodyStart + 1 + metadataIndex),
                Metadata = ParseMetadata(text[(argumentsStart + 1)..argumentsEnd])
            };

            ScanMembers(header, body, type);
            types.Add(type);
            typeIndex = bodyEnd + 1;
        }

        var enumIndex = 0;
        while (true)
        {
            var annotation = text.IndexOf("GBT_Enum", enumIndex, StringComparison.Ordinal);
            if (annotation < 0)
            {
                break;
            }

            var argumentsStart = text.IndexOf('(', annotation);
            var argumentsEnd = FindMatching(text, argumentsStart, '(', ')');
            var afterAnnotation = argumentsEnd + 1;
            var declaration = EnumDeclarationRegex().Match(text, afterAnnotation);
            if (!declaration.Success)
            {
                throw new InvalidOperationException($"{header}: GBT_Enum must be followed by an enum declaration.");
            }

            var bodyStart = declaration.Index + declaration.Length - 1;
            var bodyEnd = FindMatching(text, bodyStart, '{', '}');
            var body = text[(bodyStart + 1)..bodyEnd];
            var name = declaration.Groups["name"].Value;
            var namespaceName = FindNamespace(text[..annotation]);
            var qualifiedName = string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}::{name}";
            var underlyingTypeName = declaration.Groups["underlying"].Success
                ? NormalizeTypeName(declaration.Groups["underlying"].Value)
                : "int";

            var enumInfo = new ReflectedEnum
            {
                Module = module,
                SourceFile = header,
                Name = name,
                QualifiedName = qualifiedName,
                UnderlyingTypeName = underlyingTypeName,
                Metadata = ParseMetadata(text[(argumentsStart + 1)..argumentsEnd])
            };

            ScanEnumValues(header, body, enumInfo);
            enums.Add(enumInfo);
            enumIndex = bodyEnd + 1;
        }

        return new ReflectedModule(types, enums);
    }

    private static void ScanMembers(string header, string body, ReflectedType type)
    {
        foreach (Match match in FieldRegex().Matches(body))
        {
            var declaration = match.Groups["decl"].Value.Trim();
            declaration = declaration.Split('=', 2)[0].Trim();
            var (fieldType, fieldName) = SplitTypeAndName(header, declaration);
            ValidateReflectableType(header, fieldType);

            type.Fields.Add(new ReflectedField
            {
                Name = fieldName,
                TypeName = NormalizeTypeName(fieldType),
                Metadata = ParseMetadata(match.Groups["meta"].Value)
            });
        }

        foreach (Match match in MethodRegex().Matches(body))
        {
            var prefix = match.Groups["prefix"].Value.Trim();
            var isStatic = false;
            if (prefix.StartsWith("static ", StringComparison.Ordinal))
            {
                isStatic = true;
                prefix = prefix["static ".Length..].Trim();
            }

            var (returnType, methodName) = SplitTypeAndName(header, prefix);
            var metadata = ParseMetadata(match.Groups["meta"].Value);
            isStatic = isStatic || metadata.Any(entry => entry.Key == "Static");
            ValidateReflectableType(header, returnType, allowVoid: true);

            var method = new ReflectedMethod
            {
                Name = methodName,
                ReflectedName = metadata.FirstOrDefault(entry => entry.Key == "CallName")?.Value ?? methodName,
                ReturnTypeName = NormalizeTypeName(returnType),
                IsConst = match.Groups["const"].Success || metadata.Any(entry => entry.Key == "Const"),
                IsStatic = isStatic,
                Metadata = metadata
            };

            var parameters = SplitTopLevel(match.Groups["params"].Value, ',');
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i].Trim();
                if (parameter.Length == 0 || parameter == "void")
                {
                    continue;
                }

                parameter = parameter.Split('=', 2)[0].Trim();
                var (parameterType, parameterName) = TrySplitTypeAndName(parameter, $"Arg{i}");
                ValidateReflectableType(header, parameterType);
                method.Parameters.Add(new ReflectedParameter
                {
                    Name = parameterName,
                    TypeName = NormalizeTypeName(parameterType)
                });
            }

            type.Methods.Add(method);
        }
    }

    private static void ScanEnumValues(string header, string body, ReflectedEnum enumInfo)
    {
        foreach (var rawValue in SplitTopLevel(body, ','))
        {
            var valueText = rawValue.Trim();
            if (valueText.Length == 0)
            {
                continue;
            }

            var metadata = new List<ReflectedMetadata>();
            var metadataMatch = EnumValueRegex().Match(valueText);
            if (metadataMatch.Success)
            {
                metadata = ParseMetadata(metadataMatch.Groups["meta"].Value);
                valueText = metadataMatch.Groups["decl"].Value.Trim();
            }

            var valueName = valueText.Split('=', 2)[0].Trim();
            if (!EnumValueNameRegex().IsMatch(valueName))
            {
                throw new InvalidOperationException($"{header}: could not parse enum value '{rawValue.Trim()}'.");
            }

            enumInfo.Values.Add(new ReflectedEnumValue
            {
                Name = valueName,
                Metadata = metadata
            });
        }
    }

    private static string GenerateModuleHeader(string moduleName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#pragma once");
        builder.AppendLine();
        builder.AppendLine("namespace GBT");
        builder.AppendLine("{");
        builder.AppendLine("    class ReflectionRegistry;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("namespace GBT::Generated");
        builder.AppendLine("{");
        builder.AppendLine($"    void Register{moduleName}ReflectionTypes(ReflectionRegistry& Registry);");
        builder.AppendLine("}");
        builder.AppendLine();
        return builder.ToString();
    }

    private static string GenerateTypeMetadataHeader(Module module, string sourceFile, IEnumerable<ReflectedType> types)
    {
        var metadataMacroPrefix = GetTypeMetadataMacroPrefix(module, sourceFile);
        var builder = new StringBuilder();
        builder.AppendLine("#pragma once");
        builder.AppendLine();
        builder.AppendLine("#include \"Object/ReflectionMacros.h\"");
        builder.AppendLine();
        builder.AppendLine("#undef GBT_TypeMetadata");
        builder.AppendLine($"#define GBT_TypeMetadata() {metadataMacroPrefix}(__LINE__)");
        builder.AppendLine($"#define {metadataMacroPrefix}(Line) GBT_JOIN({metadataMacroPrefix}_LINE_, Line)");
        builder.AppendLine();
        foreach (var type in types)
        {
            builder.AppendLine($"#define {metadataMacroPrefix}_LINE_{type.MetadataLine} \\");
            builder.AppendLine("public: \\");
            builder.AppendLine($"    using This = {type.QualifiedName}; \\");
            builder.AppendLine($"    using Base = {GetBaseTypeForMetadata(type)}; \\");
            builder.AppendLine("    static const ::GBT::TypeInfo* GetStaticType(); \\");
            builder.AppendLine($"    const ::GBT::TypeInfo* GetType() const {GetOverrideSpecifier(type)};");
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string GetTypeMetadataMacroPrefix(Module module, string sourceFile)
    {
        var relativePath = Path.GetRelativePath(module.ModuleDirectory, sourceFile);
        var pathWithoutExtension = Path.Combine(
            Path.GetDirectoryName(relativePath) ?? "",
            Path.GetFileNameWithoutExtension(relativePath));
        var macroSuffix = Regex.Replace($"{module.Name}_{pathWithoutExtension}", "[^A-Za-z0-9_]", "_");
        return $"GBT_TYPE_METADATA_{macroSuffix}";
    }

    private static string GenerateSource(string sourceRoot, string moduleName, IReadOnlyCollection<ReflectedType> types, IReadOnlyCollection<ReflectedEnum> enums)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// Generated by GBT. Do not edit.");
        builder.AppendLine($"#include \"{moduleName}.gen.h\"");
        builder.AppendLine();
        builder.AppendLine("#include \"Object/ReflectionRegistry.h\"");
        builder.AppendLine();

        foreach (var include in types.Select(type => type.SourceFile)
            .Concat(enums.Select(enumInfo => enumInfo.SourceFile))
            .Select(path => path.Replace("\\", "/"))
            .Distinct())
        {
            builder.AppendLine($"#include \"{include}\"");
        }

        builder.AppendLine();
        builder.AppendLine("namespace GBT::Generated");
        builder.AppendLine("{");

        foreach (var type in types)
        {
            GenerateTypeBuilder(builder, type);
        }

        foreach (var enumInfo in enums)
        {
            GenerateEnumBuilder(builder, enumInfo);
        }

        foreach (var type in types)
        {
            GenerateTypeFunctions(builder, type);
        }

        builder.AppendLine($"    void Register{moduleName}ReflectionTypes(ReflectionRegistry& Registry)");
        builder.AppendLine("    {");
        foreach (var type in types)
        {
            builder.AppendLine($"        Registry.RegisterType(Build{type.Name}TypeInfo());");
        }
        foreach (var enumInfo in enums)
        {
            builder.AppendLine($"        Registry.RegisterEnum(Build{enumInfo.Name}EnumInfo());");
        }
        builder.AppendLine("    }");
        builder.AppendLine("} // namespace GBT::Generated");

        return builder.ToString();
    }

    private static void GenerateTypeFunctions(StringBuilder builder, ReflectedType type)
    {
        builder.AppendLine("} // namespace GBT::Generated");
        builder.AppendLine();
        builder.AppendLine($"const ::GBT::TypeInfo* {type.QualifiedName}::GetStaticType()");
        builder.AppendLine("{");
        builder.AppendLine($"    return ::GBT::ReflectionRegistry::Get().FindType(\"{type.QualifiedName}\");");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"const ::GBT::TypeInfo* {type.QualifiedName}::GetType() const");
        builder.AppendLine("{");
        builder.AppendLine("    return GetStaticType();");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("namespace GBT::Generated");
        builder.AppendLine("{");
    }

    private static void GenerateTypeBuilder(StringBuilder builder, ReflectedType type)
    {
        builder.AppendLine($"    static TypeInfo Build{type.Name}TypeInfo()");
        builder.AppendLine("    {");
        builder.AppendLine("        TypeInfo Info;");
        builder.AppendLine($"        Info.Name = \"{type.Name}\";");
        builder.AppendLine($"        Info.QualifiedName = \"{type.QualifiedName}\";");
        builder.AppendLine($"        Info.ModuleName = \"{type.Module.Name}\";");
        builder.AppendLine($"        Info.SourceFile = \"{type.SourceFile.Replace("\\", "/")}\";");
        builder.AppendLine($"        Info.DeclarationKind = TypeDeclarationKind::{(type.DeclarationKind == "struct" ? "Struct" : "Class")};");
        builder.AppendLine($"        Info.BaseTypeName = \"{type.BaseTypeName ?? ""}\";");
        builder.AppendLine("        Info.Flags = TypeFlags::None;");
        if (type.DeclarationKind == "struct")
        {
            builder.AppendLine("        Info.Flags = static_cast<TypeFlags>(static_cast<UInt32>(Info.Flags) | static_cast<UInt32>(TypeFlags::ValueType));");
        }
        builder.AppendLine($"        if constexpr (std::is_base_of_v<Object, {type.QualifiedName}>)");
        builder.AppendLine("        {");
        builder.AppendLine("            Info.Flags = static_cast<TypeFlags>(static_cast<UInt32>(Info.Flags) | static_cast<UInt32>(TypeFlags::Object));");
        builder.AppendLine("        }");
        if (type.Metadata.Any(entry => entry.Key == "Abstract"))
        {
            builder.AppendLine("        Info.Flags = static_cast<TypeFlags>(static_cast<UInt32>(Info.Flags) | static_cast<UInt32>(TypeFlags::Abstract));");
        }
        if (CanGenerateObjectFactory(type))
        {
            builder.AppendLine($"        Info.Factory = []() -> RefPtr<Object> {{");
            builder.AppendLine($"            if constexpr (std::is_base_of_v<Object, {type.QualifiedName}> && std::is_default_constructible_v<{type.QualifiedName}>)");
            builder.AppendLine("            {");
            builder.AppendLine($"                return MakeRef<{type.QualifiedName}>();");
            builder.AppendLine("            }");
            builder.AppendLine("            else");
            builder.AppendLine("            {");
            builder.AppendLine("                return nullptr;");
            builder.AppendLine("            }");
            builder.AppendLine("        };");
        }

        AppendMetadata(builder, "Info.Metadata", type.Metadata);
        foreach (var field in type.Fields)
        {
            GenerateField(builder, type, field);
        }

        foreach (var method in type.Methods)
        {
            GenerateMethod(builder, type, method);
        }

        builder.AppendLine("        return Info;");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static bool CanGenerateObjectFactory(ReflectedType type)
    {
        if (type.Metadata.Any(entry => entry.Key == "Abstract"))
        {
            return false;
        }

        return type.DeclarationKind == "class";
    }

    private static void GenerateEnumBuilder(StringBuilder builder, ReflectedEnum enumInfo)
    {
        builder.AppendLine($"    static EnumInfo Build{enumInfo.Name}EnumInfo()");
        builder.AppendLine("    {");
        builder.AppendLine("        EnumInfo Info;");
        builder.AppendLine($"        Info.Name = \"{enumInfo.Name}\";");
        builder.AppendLine($"        Info.QualifiedName = \"{enumInfo.QualifiedName}\";");
        builder.AppendLine($"        Info.ModuleName = \"{enumInfo.Module.Name}\";");
        builder.AppendLine($"        Info.SourceFile = \"{enumInfo.SourceFile.Replace("\\", "/")}\";");
        builder.AppendLine($"        Info.UnderlyingTypeName = \"{enumInfo.UnderlyingTypeName}\";");
        builder.AppendLine($"        Info.TypeId = std::type_index(typeid({enumInfo.QualifiedName}));");
        AppendMetadata(builder, "Info.Metadata", enumInfo.Metadata);
        foreach (var value in enumInfo.Values)
        {
            builder.AppendLine("        {");
            builder.AppendLine("            EnumValueInfo Value;");
            builder.AppendLine($"            Value.Name = \"{value.Name}\";");
            builder.AppendLine($"            Value.Value = static_cast<SInt64>({enumInfo.QualifiedName}::{value.Name});");
            AppendMetadata(builder, "Value.Metadata", value.Metadata, 3);
            builder.AppendLine("            Info.Values.emplace_back(std::move(Value));");
            builder.AppendLine("        }");
        }
        builder.AppendLine("        return Info;");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void GenerateField(StringBuilder builder, ReflectedType type, ReflectedField field)
    {
        var readOnly = field.Metadata.Any(entry => entry.Key == "ReadOnly");
        var readWrite = field.Metadata.Any(entry => entry.Key == "ReadWrite") || !readOnly;
        builder.AppendLine("        {");
        builder.AppendLine("            FieldInfo Field;");
        builder.AppendLine($"            Field.Name = \"{field.Name}\";");
        builder.AppendLine($"            Field.TypeName = \"{field.TypeName}\";");
        builder.AppendLine($"            Field.Flags = FieldFlags::{(readOnly ? "ReadOnly" : "ReadWrite")};");
        AppendMetadata(builder, "Field.Metadata", field.Metadata, 3);
        builder.AppendLine($"            Field.AddressGetter = [](const void* Instance) -> const void* {{ return &static_cast<const {type.QualifiedName}*>(Instance)->{field.Name}; }};");
        if (readWrite)
        {
            builder.AppendLine($"            Field.MutableAddressGetter = [](void* Instance) -> void* {{ return &static_cast<{type.QualifiedName}*>(Instance)->{field.Name}; }};");
        }
        builder.AppendLine("            Info.Fields.emplace_back(std::move(Field));");
        builder.AppendLine("        }");
    }

    private static void GenerateMethod(StringBuilder builder, ReflectedType type, ReflectedMethod method)
    {
        builder.AppendLine("        {");
        builder.AppendLine("            MethodInfo Method;");
        builder.AppendLine($"            Method.Name = \"{method.ReflectedName}\";");
        builder.AppendLine($"            Method.NativeName = \"{method.Name}\";");
        builder.AppendLine($"            Method.ReturnTypeName = \"{method.ReturnTypeName}\";");
        builder.AppendLine($"            Method.Flags = {GetMethodFlagsExpression(method)};");
        AppendMetadata(builder, "Method.Metadata", method.Metadata, 3);
        foreach (var parameter in method.Parameters)
        {
            builder.AppendLine($"            Method.Parameters.push_back(ParameterInfo{{\"{parameter.Name}\", \"{parameter.TypeName}\"}});");
        }

        builder.Append($"            Method.Invoker = [](void* Instance, void* ReturnValue, std::span<void*> Args) {{ ");
        if (!method.IsStatic)
        {
            builder.Append($"auto* TypedInstance = static_cast<{type.QualifiedName}*>(Instance); ");
        }
        if (method.ReturnTypeName == "void")
        {
            builder.Append(method.IsStatic ? $"{type.QualifiedName}::{method.Name}(" : $"TypedInstance->{method.Name}(");
            AppendMethodArguments(builder, method);
            builder.Append("); };");
        }
        else
        {
            builder.Append(method.IsStatic
                ? $"::GBT::WriteMethodReturn(ReturnValue, {type.QualifiedName}::{method.Name}("
                : $"::GBT::WriteMethodReturn(ReturnValue, TypedInstance->{method.Name}(");
            AppendMethodArguments(builder, method);
            builder.Append(")); };");
        }
        builder.AppendLine();
        builder.AppendLine("            Info.Methods.emplace_back(std::move(Method));");
        builder.AppendLine("        }");
    }

    private static string GetMethodFlagsExpression(ReflectedMethod method)
    {
        var flags = new List<string>();
        if (method.IsConst)
        {
            flags.Add("MethodFlags::Const");
        }

        if (method.IsStatic)
        {
            flags.Add("MethodFlags::Static");
        }

        if (flags.Count == 0)
        {
            return "MethodFlags::None";
        }

        return $"static_cast<MethodFlags>({string.Join(" | ", flags.Select(flag => $"static_cast<UInt32>({flag})"))})";
    }

    private static void AppendMethodArguments(StringBuilder builder, ReflectedMethod method)
    {
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            builder.Append($"::GBT::ReadMethodArgument<{method.Parameters[i].TypeName}>(Args[{i}])");
        }
    }

    private static void AppendMetadata(StringBuilder builder, string expression, IReadOnlyCollection<ReflectedMetadata> metadata, int indent = 2)
    {
        var spaces = new string(' ', indent * 4);
        foreach (var entry in metadata)
        {
            builder.AppendLine($"{spaces}{expression}.push_back(MetadataEntry{{\"{entry.Key}\", \"{entry.Value ?? "true"}\"}});");
        }
    }

    private static string GenerateAllSource(IReadOnlyCollection<ReflectedType> types, IReadOnlyCollection<ReflectedEnum> enums)
    {
        var modules = types.Select(type => type.Module.Name)
            .Concat(enums.Select(enumInfo => enumInfo.Module.Name))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var builder = new StringBuilder();
        builder.AppendLine("// Generated by GBT. Do not edit.");
        builder.AppendLine("#include \"Object/ReflectionRegistry.h\"");
        builder.AppendLine();
        builder.AppendLine("namespace GBT::Generated");
        builder.AppendLine("{");
        foreach (var module in modules)
        {
            builder.AppendLine($"    void Register{module}ReflectionTypes(ReflectionRegistry& Registry);");
        }
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("namespace GBT");
        builder.AppendLine("{");
        builder.AppendLine("    void RegisterGeneratedReflectionTypes()");
        builder.AppendLine("    {");
        builder.AppendLine("        auto& Registry = ReflectionRegistry::Get();");
        foreach (var module in modules)
        {
            builder.AppendLine($"        Generated::Register{module}ReflectionTypes(Registry);");
        }
        builder.AppendLine("    }");
        builder.AppendLine("} // namespace GBT");
        return builder.ToString();
    }

    private static string GetBaseTypeForMetadata(ReflectedType type)
    {
        return string.IsNullOrWhiteSpace(type.BaseTypeName) ? "void" : type.BaseTypeName;
    }

    private static string GetGeneratedHeaderIncludePath(Module module, string sourceFile)
    {
        var relativePath = Path.GetRelativePath(module.ModuleDirectory, sourceFile);
        var directory = Path.GetDirectoryName(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(relativePath) + ".gen.h";
        return string.IsNullOrEmpty(directory)
            ? fileName
            : Path.Combine(directory, fileName);
    }

    private static string GetOverrideSpecifier(ReflectedType type)
    {
        return type.BaseTypeName is "Object" or "GBT::Object" ? "override" : "";
    }

    private static List<ReflectedMetadata> ParseMetadata(string text)
    {
        return SplitTopLevel(text, ',')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Select(part =>
            {
                var pieces = part.Split('=', 2);
                return new ReflectedMetadata(pieces[0].Trim(), pieces.Length == 2 ? pieces[1].Trim().Trim('"') : null);
            })
            .ToList();
    }

    private static void ValidateReflectableType(string header, string typeName, bool allowVoid = false)
    {
        var normalized = NormalizeTypeName(typeName);
        if (allowVoid && normalized == "void")
        {
            return;
        }

        if (KnownTypeRegex().IsMatch(normalized)
            || normalized is "String" or "StringView" or "std::string" or "std::string_view" or "const char*"
            || normalized.StartsWith("std::vector<", StringComparison.Ordinal)
            || normalized.StartsWith("std::unordered_map<", StringComparison.Ordinal)
            || normalized.StartsWith("RefPtr<", StringComparison.Ordinal)
            || normalized.EndsWith('*')
            || normalized.EndsWith('&')
            || normalized.Contains("::", StringComparison.Ordinal)
            || char.IsUpper(normalized.TrimStart("const ".ToCharArray())[0]))
        {
            return;
        }

        throw new InvalidOperationException($"{header}: unsupported reflected type '{typeName}'.");
    }

    private static void ValidateReflectedTypeReferences(IReadOnlyCollection<ReflectedType> types, IReadOnlyCollection<ReflectedEnum> enums)
    {
        var knownTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Object",
            "GBT::Object"
        };

        foreach (var type in types)
        {
            knownTypes.Add(type.Name);
            knownTypes.Add(type.QualifiedName);
        }

        foreach (var enumInfo in enums)
        {
            knownTypes.Add(enumInfo.Name);
            knownTypes.Add(enumInfo.QualifiedName);
        }

        foreach (var type in types)
        {
            foreach (var field in type.Fields)
            {
                EnsureReflectableType(type.SourceFile, field.TypeName, knownTypes);
            }

            foreach (var method in type.Methods)
            {
                EnsureReflectableType(type.SourceFile, method.ReturnTypeName, knownTypes, allowVoid: true);
                foreach (var parameter in method.Parameters)
                {
                    EnsureReflectableType(type.SourceFile, parameter.TypeName, knownTypes);
                }
            }
        }
    }

    private static void EnsureReflectableType(string sourceFile, string typeName, IReadOnlySet<string> knownTypes, bool allowVoid = false)
    {
        var normalized = NormalizeTypeName(typeName);
        if (allowVoid && normalized == "void")
        {
            return;
        }

        if (KnownTypeRegex().IsMatch(normalized)
            || normalized is "String" or "StringView" or "std::string" or "std::string_view" or "const char*")
        {
            return;
        }

        if (TryGetTemplateArgument(normalized, "RefPtr", out var refPtrArgument))
        {
            EnsureReflectableType(sourceFile, refPtrArgument, knownTypes);
            return;
        }

        if (TryGetTemplateArgument(normalized, "std::vector", out var vectorArgument))
        {
            EnsureReflectableType(sourceFile, vectorArgument, knownTypes);
            return;
        }

        if (TryGetTemplateArgument(normalized, "std::unordered_map", out var mapArguments))
        {
            var arguments = SplitTopLevel(mapArguments, ',');
            if (arguments.Count != 2)
            {
                throw new InvalidOperationException($"{sourceFile}: reflected unordered_map type must have exactly two template arguments: '{typeName}'.");
            }

            EnsureReflectableType(sourceFile, arguments[0], knownTypes);
            EnsureReflectableType(sourceFile, arguments[1], knownTypes);
            return;
        }

        var unwrapped = normalized;
        if (unwrapped.StartsWith("const ", StringComparison.Ordinal))
        {
            unwrapped = unwrapped["const ".Length..].Trim();
        }

        unwrapped = unwrapped.TrimEnd('*', '&').Trim();
        if (knownTypes.Contains(unwrapped))
        {
            return;
        }

        throw new InvalidOperationException($"{sourceFile}: reflected API uses non-reflected type '{typeName}'. Add GBT_Type metadata or remove it from reflection.");
    }

    private static bool TryGetTemplateArgument(string typeName, string templateName, out string argument)
    {
        argument = "";
        var prefix = $"{templateName}<";
        if (!typeName.StartsWith(prefix, StringComparison.Ordinal) || !typeName.EndsWith('>'))
        {
            return false;
        }

        argument = typeName[prefix.Length..^1].Trim();
        return true;
    }

    private static string NormalizeTypeName(string typeName)
    {
        return Regex.Replace(typeName.Trim(), "\\s+", " ")
            .Replace(" *", "*", StringComparison.Ordinal)
            .Replace("* ", "*", StringComparison.Ordinal)
            .Replace(" &", "&", StringComparison.Ordinal)
            .Replace("& ", "&", StringComparison.Ordinal);
    }

    private static (string Type, string Name) SplitTypeAndName(string header, string declaration)
    {
        var match = TypeNameRegex().Match(declaration.Trim());
        if (!match.Success)
        {
            throw new InvalidOperationException($"{header}: could not parse declaration '{declaration}'.");
        }

        return (match.Groups["type"].Value.Trim(), match.Groups["name"].Value.Trim());
    }

    private static (string Type, string Name) TrySplitTypeAndName(string declaration, string fallbackName)
    {
        var match = TypeNameRegex().Match(declaration.Trim());
        return match.Success
            ? (match.Groups["type"].Value.Trim(), match.Groups["name"].Value.Trim())
            : (declaration.Trim(), fallbackName);
    }

    private static string? ParseBaseType(string baseTypes)
    {
        if (string.IsNullOrWhiteSpace(baseTypes))
        {
            return null;
        }

        var firstBase = SplitTopLevel(baseTypes, ',').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstBase))
        {
            return null;
        }

        return firstBase.Replace("public ", "", StringComparison.Ordinal)
            .Replace("protected ", "", StringComparison.Ordinal)
            .Replace("private ", "", StringComparison.Ordinal)
            .Trim();
    }

    private static string FindNamespace(string prefix)
    {
        var matches = NamespaceRegex().Matches(prefix);
        return matches.Count == 0 ? "" : matches[^1].Groups["name"].Value;
    }

    private static List<string> SplitTopLevel(string text, char delimiter)
    {
        var result = new List<string>();
        var start = 0;
        var angle = 0;
        var paren = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<': angle++; break;
                case '>': angle--; break;
                case '(': paren++; break;
                case ')': paren--; break;
                default:
                    if (text[i] == delimiter && angle == 0 && paren == 0)
                    {
                        result.Add(text[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }
        result.Add(text[start..]);
        return result;
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        if (openIndex < 0)
        {
            throw new InvalidOperationException($"Could not find '{open}'.");
        }

        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Could not find matching '{close}'.");
    }

    private static int CountLines(string text, int endExclusive)
    {
        var lines = 1;
        for (var i = 0; i < endExclusive && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", match => PreserveNewlines(match.Value), RegexOptions.Singleline);
        text = Regex.Replace(text, @"//.*?$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[^\S\r\n]*#.*?$", "", RegexOptions.Multiline);
        return text;
    }

    private static string PreserveNewlines(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void WriteFileIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [GeneratedRegex(@"\G\s*(?<kind>class|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*(?<bases>[^\{]+))?\s*\{", RegexOptions.Singleline)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"\G\s*enum(?:\s+class)?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*(?<underlying>[^\{]+))?\s*\{", RegexOptions.Singleline)]
    private static partial Regex EnumDeclarationRegex();

    [GeneratedRegex(@"GBT_Field\s*\((?<meta>[^\)]*)\)\s*(?<decl>[^;{}]+);", RegexOptions.Singleline)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"GBT_Method\s*\((?<meta>[^\)]*)\)\s*(?<prefix>[^\(\);{}]+?)\s*\((?<params>[^\)]*)\)\s*(?<const>const)?\s*(?:;|\{)", RegexOptions.Singleline)]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"^GBT_EnumValue\s*\((?<meta>[^\)]*)\)\s*(?<decl>.+)$", RegexOptions.Singleline)]
    private static partial Regex EnumValueRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnumValueNameRegex();

    [GeneratedRegex(@"^(?<type>.+?[\s\*&])(?<name>[A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex TypeNameRegex();

    [GeneratedRegex(@"namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_:]*)\s*\{")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"^(void|bool|float|double|char|wchar_t|short|int|long|unsigned\s+short|unsigned\s+int|unsigned\s+long|UInt8|UInt16|UInt32|UInt64|SInt8|SInt16|SInt32|SInt64|USize|SSize|Byte|Char|WideChar|SChar|Short|SInt|SLong|UShort|UInt|ULong|UChar)$")]
    private static partial Regex KnownTypeRegex();
}
