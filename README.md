# GBT

GBT is a self-contained build foundation for C++ projects. It provides:

- CMake project preparation and module generation.
- A C# module description layer using `*.GBTModule.cs` files.
- Generated vcpkg manifests backed by the bundled `Tools/vcpkg` submodule.
- Build-time reflection generation.
- A small C++ object/reflection runtime in the `GBT` module.

## Client Integration

The intended top-level `CMakeLists.txt` shape is:

```cmake
cmake_minimum_required(VERSION 3.30)

include(Tools/GrapesBuildTool/CMake/GBT.cmake)
GBT_PrepareProject()

project(MyProject)

GBT_GenerateProject()
```

`GBT_PrepareProject()` runs before `project(...)` so the generated vcpkg
manifest and bundled vcpkg toolchain are available when CMake enables languages.
`GBT_GenerateProject()` runs after `project(...)` and includes generated module
CMake, reflection generation, and `compile_commands.json` copying.

The misspelled compatibility alias `GBP_GenerateProject()` is also available.

## Presets

Clients may include this repository's presets:

```json
{
  "version": 9,
  "include": [
    "Tools/GrapesBuildTool/CMakePresets.json"
  ],
  "configurePresets": [
    {
      "name": "debug",
      "inherits": [ "gbt-debug" ]
    }
  ]
}
```

## Module Files

Modules are described with C# files named `Name.GBTModule.cs`:

```csharp
using GBT.BuildTool;

public sealed class Core : Module
{
    public Core()
    {
        ExternalDependencies.Add("spdlog");
        FindPackages.Add(new PackageReference("spdlog"));
        PublicLinks.Add("spdlog::spdlog");
    }
}
```

Module target aliases default to the CMake project namespace. For
`project(MyProject)`, the module above is exported as `MyProject::Core`. A
module can override that namespace when it needs to publish under another target
namespace:

```csharp
using GBT.BuildTool;

public sealed class Core : Module
{
    public Core()
    {
        Namespace = "Engine";
    }
}
```

## Reflection Macros

Use `GBT_Type`, `GBT_Enum`, `GBT_Field`, `GBT_Method`, and
`GBT_TypeMetadata` from `Object/ReflectionMacros.h`.
