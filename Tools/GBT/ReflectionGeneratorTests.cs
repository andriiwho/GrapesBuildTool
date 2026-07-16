namespace GBT.BuildTool;

// Dependency-free regression suite for the reflection scanner and generator.
// Run with `dotnet run --project Tools/GBT -- --mode self-test`.
internal static class ReflectionGeneratorTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"gbt-reflection-tests-{Guid.NewGuid():N}");
        try
        {
            ValidateBalancedDeclarations(root);
            ValidateMalformedAnnotationFails(root);
            ValidateAmbiguousTypeFails(root);
            Console.WriteLine("[GBT] reflection generator self-tests passed (3 cases)");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateBalancedDeclarations(string root)
    {
        const string header = """
            #pragma once
            namespace Fixture
            {
                GBT_Type()
                struct Child
                {
                    GBT_TypeMetadata();
                    GBT_Field(ReadWrite)
                    int Value = 1;
                };

                GBT_Type()
                struct Complex
                {
                    GBT_TypeMetadata();
                    GBT_Field(ReadWrite)
                    Child Aggregate = { .Value = 7 };
                    GBT_Field(ReadWrite)
                    std::vector<Child> Children = { Child{}, Child{} };
                    GBT_Field(ReadWrite)
                    int LambdaValue = [] { int local = 2; return local < 3 ? local : 3; }();
                    GBT_Method(CallName = "ReadValues")
                    const std::vector<int>& Values() const;
                };
            }
            """;
        var generated = GenerateFixture(root, "balanced", header);
        RequireContains(generated, "Field.Name = \"Aggregate\"");
        RequireContains(generated, "Field.Name = \"Children\"");
        RequireContains(generated, "Field.Name = \"LambdaValue\"");
        RequireContains(generated, "Method.Name = \"ReadValues\"");
        RequireContains(generated, "ReflectionAbiVersion == 2");
    }

    private static void ValidateMalformedAnnotationFails(string root)
    {
        const string header = """
            namespace Fixture
            {
                GBT_Type()
                struct Broken
                {
                    GBT_TypeMetadata();
                    GBT_Field(ReadWrite)
                    int MissingTerminator = 1
                };
            }
            """;
        ExpectFailure(() => GenerateFixture(root, "malformed", header), "no top-level terminator");
    }

    private static void ValidateAmbiguousTypeFails(string root)
    {
        const string header = """
            namespace First { GBT_Type() struct Item { GBT_TypeMetadata(); }; }
            namespace Second { GBT_Type() struct Item { GBT_TypeMetadata(); }; }
            namespace Fixture
            {
                GBT_Type()
                struct Owner
                {
                    GBT_TypeMetadata();
                    GBT_Field(ReadWrite)
                    Item Value;
                };
            }
            """;
        ExpectFailure(() => GenerateFixture(root, "ambiguous", header), "is ambiguous");
    }

    private static string GenerateFixture(string root, string name, string header)
    {
        var fixtureRoot = Path.Combine(root, name);
        var source = Path.Combine(fixtureRoot, "Fixture.h");
        var moduleFile = Path.Combine(fixtureRoot, "Fixture.GBTModule.toml");
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(source, header);
        File.WriteAllText(moduleFile, "// reflection generator fixture");
        var module = new Module("Fixture");
        module.SetSourceFile(moduleFile);
        var output = Path.Combine(fixtureRoot, "Generated");
        ReflectionGenerator.Generate(fixtureRoot, output, [module]);
        return File.ReadAllText(Path.Combine(output, "reflection", "Fixture", "Fixture.gen.cpp"));
    }

    private static void RequireContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Reflection self-test expected generated text '{expected}'.");
    }

    private static void ExpectFailure(Action action, string expectedMessage)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error) when (error.Message.Contains(expectedMessage, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"Reflection self-test expected failure containing '{expectedMessage}'.");
    }
}
