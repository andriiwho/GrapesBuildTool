# =============================================================================
# GBT integration
# =============================================================================

cmake_path(GET CMAKE_CURRENT_LIST_DIR PARENT_PATH GBT_Root)

set(GBT_OutputDir "${CMAKE_BINARY_DIR}/generated/gbt" CACHE PATH "GBT output directory")
set(GBT_GeneratedModulesFile "${GBT_OutputDir}/Modules.cmake" CACHE INTERNAL "Generated GBT module CMake file")
set(GBT_Project "${GBT_Root}/Tools/GBT/GBT.csproj")

function(GBT_GetBuildToolPlatform OutVar)
    if(EMSCRIPTEN OR CMAKE_SYSTEM_NAME STREQUAL "Emscripten")
        set(Platform "EMSCRIPTEN")
    elseif(IOS OR CMAKE_SYSTEM_NAME STREQUAL "iOS")
        set(Platform "IOS")
    elseif(ANDROID)
        set(Platform "ANDROID")
    elseif(WIN32)
        set(Platform "WINDOWS")
    elseif(APPLE)
        set(Platform "MAC")
    elseif(UNIX)
        set(Platform "LINUX")
    else()
        string(TOUPPER "${CMAKE_SYSTEM_NAME}" Platform)
    endif()

    set(${OutVar} "${Platform}" PARENT_SCOPE)
endfunction()

function(GBT_RunBuildTool Mode)
    find_program(DotNetExecutable dotnet REQUIRED)
    GBT_GetBuildToolPlatform(CurrentPlatform)

    execute_process(
            COMMAND
                "${CMAKE_COMMAND}" -E env
                "DOTNET_CLI_HOME=${GBT_OutputDir}/dotnet-home"
                "DOTNET_CLI_TELEMETRY_OPTOUT=1"
                "DOTNET_NOLOGO=1"
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
                "NUGET_PACKAGES=${GBT_OutputDir}/nuget"
                "${DotNetExecutable}" run
                --project "${GBT_Project}"
                --
                --mode "${Mode}"
                --source-root "${CMAKE_SOURCE_DIR}"
                --tool-root "${GBT_Root}"
                --output-dir "${GBT_OutputDir}"
                --platform "${CurrentPlatform}"
            WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
            RESULT_VARIABLE BuildToolResult
            OUTPUT_VARIABLE BuildToolOutput
            ERROR_VARIABLE BuildToolError
    )

    if(BuildToolOutput)
        message(STATUS "${BuildToolOutput}")
    endif()

    if(NOT BuildToolResult EQUAL 0)
        if(BuildToolError)
            message(STATUS "${BuildToolError}")
        endif()

        message(FATAL_ERROR "[GBT] failed while running '${Mode}'")
    endif()
endfunction()

function(GBT_PrepareProject)
    file(GLOB_RECURSE GBT_ModuleManifestInputs CONFIGURE_DEPENDS
            "${CMAKE_SOURCE_DIR}/*.GBTModule.toml"
    )
    file(GLOB_RECURSE GBT_BuildToolSourceInputs CONFIGURE_DEPENDS
            "${GBT_Root}/Tools/GBT/*.cs"
            "${GBT_Root}/Tools/GBT/*.csproj"
    )
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
            "${CMAKE_SOURCE_DIR}/GBTProject.toml"
            ${GBT_ModuleManifestInputs}
            ${GBT_BuildToolSourceInputs}
    )

    if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
        set(CMAKE_TOOLCHAIN_FILE "${GBT_Root}/Tools/vcpkg/scripts/buildsystems/vcpkg.cmake" CACHE FILEPATH "GBT bundled vcpkg toolchain" FORCE)
    endif()

    GBT_RunBuildTool("manifest")
    set(VCPKG_MANIFEST_DIR "${GBT_OutputDir}/vcpkg" CACHE PATH "Generated GBT vcpkg manifest directory" FORCE)
    set(VCPKG_MANIFEST_INSTALL ON CACHE BOOL "Install packages from the generated GBT vcpkg manifest" FORCE)
endfunction()

function(GBT_GenerateProject)
    include("${GBT_Root}/CMake/GBTBuildSystem.cmake")
    GBT_RunBuildTool("cmake")
    GBT_IncludeGeneratedModules()
    GBT_AddBuildTimeReflectionGeneration()
    GBT_AddCompileCommandsCopyTarget()
endfunction()

function(GBT_IncludeGeneratedModules)
    if(NOT EXISTS "${GBT_GeneratedModulesFile}")
        message(FATAL_ERROR
                "[GBT] Generated module file not found: ${GBT_GeneratedModulesFile}\n"
                "GBT should have generated it during configure."
        )
    endif()

    include("${GBT_GeneratedModulesFile}")
endfunction()

function(GBT_AddCompileCommandsCopyTarget)
    if(CMAKE_EXPORT_COMPILE_COMMANDS)
        add_custom_target(GBT_CopyCompileCommands ALL
                COMMAND "${CMAKE_COMMAND}" -E copy_if_different
                        "${CMAKE_BINARY_DIR}/compile_commands.json"
                        "${CMAKE_SOURCE_DIR}/compile_commands.json"
                COMMENT "[GBT] copying compile_commands.json to source root"
                VERBATIM
        )
    endif()
endfunction()

function(GBT_AddBuildTimeReflectionGeneration)
    find_program(DotNetExecutable dotnet REQUIRED)
    GBT_GetBuildToolPlatform(CurrentPlatform)

    get_property(ReflectionInputs GLOBAL PROPERTY GBT_ReflectionInputFiles)
    get_property(ReflectionTargets GLOBAL PROPERTY GBT_ReflectionTargets)
    get_property(GeneratedReflectionSources GLOBAL PROPERTY GBT_GeneratedReflectionSources)

    if(ReflectionInputs)
        list(REMOVE_DUPLICATES ReflectionInputs)
    endif()

    if(ReflectionTargets)
        list(REMOVE_DUPLICATES ReflectionTargets)
    endif()

    if(GeneratedReflectionSources)
        list(REMOVE_DUPLICATES GeneratedReflectionSources)
    endif()

    file(GLOB_RECURSE BuildToolInputs CONFIGURE_DEPENDS
            "${GBT_Root}/Tools/GBT/*.cs"
            "${GBT_Root}/Tools/GBT/*.csproj"
    )

    set(ReflectionStamp "${GBT_OutputDir}/reflection/Reflection.stamp")

    add_custom_command(
            OUTPUT "${ReflectionStamp}"
            COMMAND "${CMAKE_COMMAND}" -E make_directory "${GBT_OutputDir}/reflection"
            COMMAND
                "${CMAKE_COMMAND}" -E env
                "DOTNET_CLI_HOME=${GBT_OutputDir}/dotnet-home"
                "DOTNET_CLI_TELEMETRY_OPTOUT=1"
                "DOTNET_NOLOGO=1"
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
                "NUGET_PACKAGES=${GBT_OutputDir}/nuget"
                "${DotNetExecutable}" run
                --project "${GBT_Project}"
                --
                --mode "reflection"
                --source-root "${CMAKE_SOURCE_DIR}"
                --tool-root "${GBT_Root}"
                --output-dir "${GBT_OutputDir}"
                --platform "${CurrentPlatform}"
            COMMAND "${CMAKE_COMMAND}" -E touch "${ReflectionStamp}"
            DEPENDS
                ${ReflectionInputs}
                ${BuildToolInputs}
            BYPRODUCTS
                ${GeneratedReflectionSources}
            WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
            COMMENT "[GBT] generating reflection metadata"
            VERBATIM
    )

    add_custom_target(GBT_GenerateReflection DEPENDS "${ReflectionStamp}")

    foreach(TargetName IN LISTS ReflectionTargets)
        add_dependencies("${TargetName}" GBT_GenerateReflection)
    endforeach()
endfunction()
