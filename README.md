# GBT

GBT is a small CMake/C# build layer for C++ projects. It describes project
modules in C#, generates CMake targets, writes the vcpkg manifest used by the
build, and generates a simple reflection database for annotated C++ types.

It is intentionally boring to integrate: include one CMake file before
`project(...)`, call one function, call `project(...)`, then call one more
function.

## Adding It

The repository does not have to be cloned into `Tools/GrapesBuildTool`. This is
only the convention used by Grapes and by the presets shipped in this repo.

For example, this works:

```sh
git submodule add https://github.com/andriiwho/GrapesBuildTool.git External/GBT
git submodule update --init --recursive
```

Then include it from that path:

```cmake
cmake_minimum_required(VERSION 3.30)

include(External/GBT/CMake/GBT.cmake)
GBT_PrepareProject()

project(MyProject)

GBT_GenerateProject()
```

`GBT_PrepareProject()` must run before `project(...)`. It generates the vcpkg
manifest and points CMake at the bundled vcpkg toolchain when no toolchain was
already provided.

`GBT_GenerateProject()` runs after `project(...)`. It generates and includes the
module targets, wires build-time reflection generation, and copies
`compile_commands.json` to the source root when compile commands are enabled.

If you want to include `CMakePresets.json` from this repo without editing it,
use the conventional path:

```sh
git submodule add https://github.com/andriiwho/GrapesBuildTool.git Tools/GrapesBuildTool
```

Then a client preset can inherit from GBT's hidden presets:

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
    ],
    "buildPresets": [
        {
            "name": "debug",
            "configurePreset": "debug"
        }
    ]
}
```

## Modules

A module is a C# file named `Name.GBTModule.cs` under the client project's
`Source/` or `Games/` directory, or under GBT's own `Source/` directory.

The class normally has the same name as the file:

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

For `project(MyProject)`, that module becomes the target alias
`MyProject::Core`.

Static library modules are the default. Executables and interface-only modules
are explicit:

```csharp
using GBT.BuildTool;

public sealed class App : Module
{
    public App()
    {
        Kind = ModuleKind.Executable;

        PrivateLinks.Add("MyProject::Core");
    }
}
```

```csharp
using GBT.BuildTool;

public sealed class Launch : Module
{
    public Launch()
    {
        Kind = ModuleKind.Interface;

        InterfaceLinks.Add("MyProject::Core");
    }
}
```

Target aliases default to the CMake project namespace. A module can publish
under another namespace when that is useful:

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

That module is exported as `Engine::Core` instead of `MyProject::Core`.

Platform filters use `ModulePlatforms`:

```csharp
using GBT.BuildTool;

public sealed class RenderDeviceSDL : Module
{
    public RenderDeviceSDL()
    {
        Platforms = ModulePlatforms.Desktop;

        PublicLinks.Add("MyProject::RenderDevice");
        ExternalDependencies.Add(new ExternalDependency("sdl3", "vulkan"));
        FindPackages.Add(new PackageReference("SDL3"));
        PrivateLinks.Add("SDL3::SDL3");
    }
}
```

Common module properties:

```csharp
PublicLinks.Add("MyProject::Core");
PrivateLinks.Add("some_private_target");
InterfaceLinks.Add("header_only_target");

PublicDefinitions.Add("MYPROJECT_PUBLIC=1");
PrivateDefinitions.Add("MYPROJECT_PRIVATE=1");
InterfaceDefinitions.Add("MYPROJECT_HEADER_ONLY=1");

PublicIncludes.Add("/absolute/or/generated/include/path");
PrivateIncludes.Add("/private/include/path");
InterfaceIncludes.Add("/interface/include/path");

ExternalDependencies.Add("fmt");
ExternalDependencies.Add(new ExternalDependency("imgui", "docking-experimental"));

FindPackages.Add(new PackageReference("fmt"));
FindPackages.Add(new PackageReference("imgui") { Config = true, Required = true });
```

## Reflection

GBT ships a small C++ runtime module named `GBT`. Link it from whichever module
needs object/reflection types:

```csharp
using GBT.BuildTool;

public sealed class Core : Module
{
    public Core()
    {
        PublicLinks.Add("MyProject::GBT");
    }
}
```

Use the macros from `Object/ReflectionMacros.h`.

```cpp
#pragma once

#include "Object/ReflectionMacros.h"

#include <string>

#include "Core.gen.h"

namespace MyProject
{
    GBT_Type()
    struct Version
    {
        GBT_TypeMetadata();

        GBT_Field(ReadWrite)
        int Major = 0;

        GBT_Field(ReadWrite)
        int Minor = 0;

        GBT_Field(ReadWrite)
        int Patch = 0;

        GBT_Method()
        std::string ToString() const;
    };
}
```

The generated header is named after the module. If this type lives in the
`Core` module, the generated header is `Core.gen.h`.
`GBT_TypeMetadata()` expands through that header and adds the type functions
needed by the reflection runtime. The generated files are written under the
build directory; do not edit them.

Enums can be reflected too:

```cpp
#include "Object/ReflectionMacros.h"
#include "Core.gen.h"

namespace MyProject
{
    GBT_Enum()
    enum class Axis
    {
        GBT_EnumValue()
        X,

        GBT_EnumValue()
        Y,

        GBT_EnumValue()
        Z,
    };
}
```

Fields and methods currently use simple metadata tokens:

```cpp
GBT_Field(ReadOnly)
int Id = 0;

GBT_Field(ReadWrite)
float Radius = 1.0f;

GBT_Method(Const)
float GetRadius() const;

GBT_Method(CallName = "SetRadius")
void SetRadius(float Value);
```

## Registering Reflection

At startup, call the generated registration function once before using the
database:

```cpp
#include "Object/ReflectionRegistry.h"

int main()
{
    GBT::RegisterGeneratedReflectionTypes();

    const GBT::TypeInfo* Type =
        GBT::ReflectionRegistry::Get().FindType("MyProject::Version");

    return Type ? 0 : 1;
}
```

If you use an engine launch layer, put the call there rather than in every
game/application executable. The generated registrar lives in one of the
generated reflection sources and registers every reflected type and enum found
in enabled modules.

For reflected classes that inherit from `GBT::Object`, the registry can also
mark object types and create default-constructible instances:

```cpp
#include "Object/Object.h"
#include "Object/ReflectionMacros.h"

#include <string>

#include "Core.gen.h"

namespace MyProject
{
    GBT_Type()
    class TextureAsset : public GBT::Object
    {
    public:
        GBT_TypeMetadata();

        GBT_Field(ReadWrite)
        std::string Path;
    };
}
```

## Notes

The build expects CMake 3.30 or newer, Ninja, a C++23 compiler, and a .NET SDK.
The bundled vcpkg submodule is used by default, but a project can pass its own
`CMAKE_TOOLCHAIN_FILE` before `GBT_PrepareProject()` if it wants to own the
toolchain itself.
