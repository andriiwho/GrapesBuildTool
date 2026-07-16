# GBT

GBT is a small CMake/.NET build layer for C++ projects. It describes project
modules in TOML, generates CMake targets, writes the vcpkg manifest used by the
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

A project declares module search roots in `GBTProject.toml`:

```toml
SchemaVersion = 1

[Project]
Name = "MyProject"
ModuleRoots = ["Source", "Games", "External/GBT/Source"]
```

Each module is a `Name.GBTModule.toml` file below one of those roots. Its
directory owns the module's discovered source and header files:

```toml
SchemaVersion = 1

[Module]
Name = "Core"

[[Dependency]]
Name = "spdlog"
Package = "spdlog"
Target = "spdlog::spdlog"
Visibility = "Public"
```

For `project(MyProject)`, that module becomes the target alias
`MyProject::Core`.

Static library modules are the default. Executables and interface-only modules
are explicit:

```toml
[Module]
Name = "App"
Kind = "Executable"

[Links]
Private = ["MyProject::Core"]
```

```toml
[Module]
Name = "Launch"
Kind = "Interface"

[Links]
Interface = ["MyProject::Core"]
```

Target aliases default to the CMake project namespace. A module can publish
under another namespace when that is useful:

```toml
[Module]
Name = "Core"
Namespace = "Engine"
```

That module is exported as `Engine::Core` instead of `MyProject::Core`.

Platform filters use a strict list of `Windows`, `Mac`, `Linux`, `Android`,
`Emscripten`, `Ios`, `Desktop`, or `All`:

```toml
[Module]
Name = "RenderDeviceSDL"
Platforms = ["Desktop"]

[Links]
Public = ["MyProject::RenderDevice"]

[[Dependency]]
Name = "sdl3"
Features = ["vulkan"]
Package = "SDL3"
Target = "SDL3::SDL3"
Visibility = "Private"
```

Common module properties:

```toml
[Links]
Public = ["MyProject::Core"]
Private = ["some_private_target"]
Interface = ["header_only_target"]

[Definitions]
Public = ["MYPROJECT_PUBLIC=1"]
Private = ["MYPROJECT_PRIVATE=1"]
Interface = ["MYPROJECT_HEADER_ONLY=1"]

[Includes]
Public = ["Public"]
Private = ["Private"]
Interface = ["Generated"]

[[Dependency]]
Name = "imgui"
Features = ["docking-experimental"]
Package = "imgui"
Target = "imgui::imgui"
Visibility = "Public"
Config = true
Required = true
```

## Reflection

GBT ships a small C++ runtime module named `GBT`. Link it from whichever module
needs object/reflection types:

```toml
[Module]
Name = "Core"

[Links]
Public = ["MyProject::GBT"]
```

Use the macros from `Object/ReflectionMacros.h`.

```cpp
#pragma once

#include "Object/ReflectionMacros.h"

#include <string>

#include "Core/Version.gen.h"

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

The generated metadata header follows the source path inside the module. If
this type lives in `Source/Core/Core/Version.h` and the module root is
`Source/Core`, the generated include is `Core/Version.gen.h`.
`GBT_TypeMetadata()` expands through that per-source header and adds the type
functions needed by the reflection runtime.

GBT also writes a module-level header such as `Core.gen.h`. That file is used
by generated module registration code; reflected source headers should include
their own `ModuleRelativePath/FileName.gen.h` file instead. The generated files
are written under the build directory; do not edit them.

Enums can be reflected too:

```cpp
#include "Object/ReflectionMacros.h"
#include "Core/Axis.gen.h"

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

## Reflection Reliability And Runtime Types

Reflected member declarations are scanned with balanced delimiter and quoted
literal tracking. Aggregate initializers, nested templates, multiline members,
and lambdas therefore do not hide annotated fields. An annotation that cannot
be consumed as one complete declaration stops generation with a file and line
diagnostic instead of silently omitting metadata.

Each generated field exposes a `ValueTypeInfo` classification (`Primitive`,
`Enum`, `Reflected`, or `Sequence`). Sequence metadata provides size, resize,
element type, and element-address operations. Proxy containers such as
`std::vector<bool>` deliberately omit unsafe element-address operations.

Qualified names are always authoritative. If multiple registered types or
enums share an unqualified name, `FindTypeByName` or `FindEnumByName` returns
null and callers must use the qualified name. Generated sources also carry a
reflection ABI assertion; stale metadata fails compilation or registration.

Run the dependency-free generator regression suite with:

```sh
dotnet run --project Tools/GBT/GBT.csproj -- --mode self-test
```

For reflected classes that inherit from `GBT::Object`, the registry can also
mark object types and create default-constructible instances:

```cpp
#include "Object/Object.h"
#include "Object/ReflectionMacros.h"

#include <string>

#include "Core/TextureAsset.gen.h"

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
