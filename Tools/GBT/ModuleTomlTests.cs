namespace GBT.BuildTool;

// Dependency-free schema regression tests for project and module TOML loading.
internal static class ModuleTomlTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"gbt-module-toml-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "GBTProject.toml");
            File.WriteAllText(projectPath, """
                SchemaVersion = 1
                [Project]
                Name = "Fixture"
                ModuleRoots = ["Source", "Games"]
                """);
            var project = ModuleToml.ReadProject(projectPath);
            Require(project.ModuleRoots.SequenceEqual(["Source", "Games"]), "Project module roots were not parsed.");

            var modulePath = Path.Combine(root, "Fixture.GBTModule.toml");
            File.WriteAllText(modulePath, """
                SchemaVersion = 1
                [Module]
                Name = "Fixture"
                Kind = "Executable"
                Platforms = ["Windows", "Linux"]
                [Links]
                Private = ["Fixture::Core"]
                [Definitions]
                Private = ["FIXTURE=1"]
                [[Dependency]]
                Name = "imgui"
                Features = ["docking-experimental"]
                Package = "imgui"
                Target = "imgui::imgui"
                Visibility = "Private"
                """);
            var module = ModuleToml.ReadModule(modulePath);
            Require(module.Name == "Fixture" && module.Kind == ModuleKind.Executable, "Module identity was not parsed.");
            Require(module.Platforms == (ModulePlatforms.Windows | ModulePlatforms.Linux), "Module platforms were not parsed.");
            Require(module.PrivateLinks.SequenceEqual(["Fixture::Core", "imgui::imgui"]), "Module links were not parsed.");
            Require(module.ExternalDependencies.Single().ToString() == "imgui[docking-experimental]", "Dependency features were not parsed.");

            File.WriteAllText(modulePath, "SchemaVersion = 1\n[Module]\nName = \"Broken\"\nUnknown = true\n");
            ExpectFailure(() => ModuleToml.ReadModule(modulePath), "Unknown key 'Unknown'");
            Console.WriteLine("[GBT] module TOML self-tests passed (3 cases)");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectFailure(Action action, string expected)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Expected module TOML failure containing '{expected}'.");
    }
}
