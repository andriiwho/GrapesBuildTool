# =============================================================================
# GBT build system
# Minimum required: CMake 3.25 (CONFIGURE_DEPENDS, cmake_path, etc.)
# =============================================================================

set(GBT_ConfigIncludeDir "${CMAKE_BINARY_DIR}/generated/gbt/include" CACHE INTERNAL "GBT generated include directory")
set(GBT_ConfigHeader "${GBT_ConfigIncludeDir}/GBTConfig.h" CACHE INTERNAL "GBT platform config header")

function(GBT_GetCurrentPlatform OutVar)
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

function(GBT_PlatformEnabled OutVar)
    set(Platforms ${ARGN})

    if(NOT Platforms)
        set(${OutVar} TRUE PARENT_SCOPE)
        return()
    endif()

    GBT_GetCurrentPlatform(CurrentPlatform)

    set(ExpandedPlatforms "")
    foreach(Platform IN LISTS Platforms)
        string(TOUPPER "${Platform}" PlatformUpper)

        if(PlatformUpper STREQUAL "DESKTOP")
            list(APPEND ExpandedPlatforms WINDOWS MAC LINUX)
        else()
            list(APPEND ExpandedPlatforms "${PlatformUpper}")
        endif()
    endforeach()

    if(CurrentPlatform IN_LIST ExpandedPlatforms OR "ALL" IN_LIST ExpandedPlatforms)
        set(${OutVar} TRUE PARENT_SCOPE)
    else()
        set(${OutVar} FALSE PARENT_SCOPE)
    endif()
endfunction()

function(GBT_RequirePascalCase Value Context)
    if(NOT "${Value}" MATCHES "^[A-Z][A-Za-z0-9]*$")
        message(FATAL_ERROR "[GBT] ${Context} must be PascalCase, got '${Value}'")
    endif()
endfunction()

function(GBT_ConfigurePlatformVariables)
    GBT_GetCurrentPlatform(CurrentPlatform)

    set(GBT_Platform "${CurrentPlatform}" CACHE INTERNAL "GBT current platform" FORCE)

    foreach(Platform IN ITEMS Windows Mac Linux Android Emscripten Ios)
        string(TOUPPER "${Platform}" PlatformId)

        if(CurrentPlatform STREQUAL "${PlatformId}")
            set(Enabled 1)
        else()
            set(Enabled 0)
        endif()

        set(GBT_Platform${Platform} "${Enabled}" CACHE INTERNAL "GBT ${PlatformId} platform flag" FORCE)
    endforeach()

    if(GBT_PlatformWindows OR GBT_PlatformMac OR GBT_PlatformLinux)
        set(Desktop 1)
    else()
        set(Desktop 0)
    endif()

    set(GBT_PlatformDesktop "${Desktop}" CACHE INTERNAL "GBT desktop platform flag" FORCE)
endfunction()

function(GBT_GenerateConfigHeader)
    file(MAKE_DIRECTORY "${GBT_ConfigIncludeDir}")
    file(WRITE "${GBT_ConfigHeader}"
            "#pragma once

#define GBT_PLATFORM_WINDOWS ${GBT_PlatformWindows}
#define GBT_PLATFORM_MAC ${GBT_PlatformMac}
#define GBT_PLATFORM_LINUX ${GBT_PlatformLinux}
#define GBT_PLATFORM_ANDROID ${GBT_PlatformAndroid}
#define GBT_PLATFORM_EMSCRIPTEN ${GBT_PlatformEmscripten}
#define GBT_PLATFORM_IOS ${GBT_PlatformIos}

#define GBT_PLATFORM_DESKTOP (GBT_PLATFORM_WINDOWS || GBT_PLATFORM_MAC || GBT_PLATFORM_LINUX)
")
endfunction()

GBT_ConfigurePlatformVariables()
GBT_GenerateConfigHeader()

# =============================================================================
# GBT_DiscoverModules(RootDir)
#
# Recursively discovers every GBTModule.cmake under RootDir and includes it.
# Each included file runs with CMAKE_CURRENT_LIST_DIR set to its own directory,
# which GBT_AddModule captures as the module root.
# =============================================================================
function(GBT_DiscoverModules RootDir)
    file(GLOB_RECURSE BuildFiles
            LIST_DIRECTORIES false
            "${RootDir}/GBTModule.cmake"
    )

    if(NOT BuildFiles)
        message(WARNING "[GBT] GBT_DiscoverModules: no GBTModule.cmake found under '${RootDir}'")
        return()
    endif()

    list(SORT BuildFiles)
    foreach(File IN LISTS BuildFiles)
        message(STATUS "[GBT] discovered module: ${File}")
        include("${File}")
    endforeach()
endfunction()

# =============================================================================
# GBT_IncludeOptionalDir(DirName PLATFORMS <platforms>)
#
# Includes <DirName>/gbt_opt.cmake when the current platform matches.
# Platform names are WINDOWS, MAC, LINUX, ANDROID, EMSCRIPTEN, IOS, DESKTOP, ALL.
# =============================================================================
function(GBT_IncludeOptionalDir DirName)
    cmake_parse_arguments(ARG
            ""
            ""
            "PLATFORMS"
            ${ARGN}
    )

    if(ARG_UNPARSED_ARGUMENTS)
        message(FATAL_ERROR
                "[GBT] GBT_IncludeOptionalDir('${DirName}'): unexpected arguments: ${ARG_UNPARSED_ARGUMENTS}"
        )
    endif()

    GBT_PlatformEnabled(Enabled ${ARG_PLATFORMS})
    if(NOT Enabled)
        GBT_GetCurrentPlatform(CurrentPlatform)
        message(STATUS
                "[GBT] optional dir skipped: ${CMAKE_CURRENT_LIST_DIR}/${DirName} "
                "(platform '${CurrentPlatform}' is not in PLATFORMS: ${ARG_PLATFORMS})"
        )
        return()
    endif()

    set(OptFile "${CMAKE_CURRENT_LIST_DIR}/${DirName}/gbt_opt.cmake")
    if(NOT EXISTS "${OptFile}")
        message(FATAL_ERROR "[GBT] optional dir '${DirName}' does not contain gbt_opt.cmake")
    endif()

    message(STATUS "[GBT] optional dir included: ${OptFile}")
    include("${OptFile}")
endfunction()

# =============================================================================
# GBT_AddModuleImpl(Name ModuleDir [keyword args])
#
# Internal implementation. ModuleDir is the directory of the GBTModule.cmake that
# declared this module.
# =============================================================================
function(GBT_AddModuleImpl ModuleName ModuleDir)
    cmake_parse_arguments(ARG
            ""
            "KIND"
            "PUBLIC_LINKS;PRIVATE_LINKS;INTERFACE_LINKS;PUBLIC_DEFINITIONS;PRIVATE_DEFINITIONS;INTERFACE_DEFINITIONS;PUBLIC_INCLUDES;PRIVATE_INCLUDES;INTERFACE_INCLUDES;EXTERNAL_DEPENDENCIES;GENERATED_SOURCES;PCH"
            ${ARGN}
    )

    GBT_RequirePascalCase("${ModuleName}" "GBT_AddModule module name")

    if(NOT ARG_KIND)
        set(ARG_KIND "static")
    endif()

    string(TOLOWER "${ARG_KIND}" Kind)
    set(ValidKinds static interface executable)
    if(NOT Kind IN_LIST ValidKinds)
        message(FATAL_ERROR
                "[GBT] GBT_AddModule('${ModuleName}'): invalid KIND '${ARG_KIND}'. "
                "Valid values: ${ValidKinds}"
        )
    endif()

    if(Kind STREQUAL "interface")
        foreach(Forbidden IN ITEMS
                PRIVATE_LINKS PUBLIC_LINKS
                PRIVATE_DEFINITIONS PUBLIC_DEFINITIONS
                PRIVATE_INCLUDES PUBLIC_INCLUDES
                PCH
        )
            if(ARG_${Forbidden})
                message(FATAL_ERROR
                        "[GBT] GBT_AddModule('${ModuleName}'): "
                        "interface modules only allow INTERFACE_* arguments"
                )
            endif()
        endforeach()
    endif()

    set(TargetName "${PROJECT_NAME}${ModuleName}")
    GBT_RequirePascalCase("${TargetName}" "GBT target name")

    set(Sources "")
    if(NOT Kind STREQUAL "interface")
        file(GLOB_RECURSE Sources CONFIGURE_DEPENDS
                "${ModuleDir}/*.c"
                "${ModuleDir}/*.cc"
                "${ModuleDir}/*.cpp"
                "${ModuleDir}/*.cxx"
                "${ModuleDir}/*.h"
                "${ModuleDir}/*.hpp"
                "${ModuleDir}/*.inl"
                "${ModuleDir}/*.natvis"
        )
        list(APPEND Sources ${ARG_GENERATED_SOURCES})
        if(ARG_GENERATED_SOURCES)
            set_source_files_properties(${ARG_GENERATED_SOURCES} PROPERTIES GENERATED TRUE)
        endif()

        if(NOT Sources)
            message(WARNING
                    "[GBT] GBT_AddModule('${ModuleName}'): no source files found under '${ModuleDir}'"
            )
        endif()
    endif()

    if(Kind STREQUAL "static")
        add_library(${TargetName} STATIC ${Sources})
    elseif(Kind STREQUAL "interface")
        add_library(${TargetName} INTERFACE)
    elseif(Kind STREQUAL "executable")
        add_executable(${TargetName} ${Sources})
        set_property(GLOBAL APPEND PROPERTY GBT_ExecutableTargets "${TargetName}")
    endif()

    if(NOT Kind STREQUAL "interface")
        file(GLOB_RECURSE ReflectionInputs CONFIGURE_DEPENDS
                "${ModuleDir}/*.h"
                "${ModuleDir}/*.hpp"
        )

        set_property(GLOBAL APPEND PROPERTY GBT_ReflectionInputFiles ${ReflectionInputs})
        set_property(GLOBAL APPEND PROPERTY GBT_ReflectionTargets "${TargetName}")
        set_property(GLOBAL APPEND PROPERTY GBT_GeneratedReflectionSources ${ARG_GENERATED_SOURCES})
    endif()

    if(NOT Kind STREQUAL "executable")
        add_library(${PROJECT_NAME}::${ModuleName} ALIAS ${TargetName})
    endif()

    if(Kind STREQUAL "interface")
        target_compile_features(${TargetName} INTERFACE cxx_std_23)
        target_link_libraries(${TargetName} INTERFACE ${ARG_INTERFACE_LINKS})
        target_compile_definitions(${TargetName} INTERFACE ${ARG_INTERFACE_DEFINITIONS})
        target_include_directories(${TargetName}
                INTERFACE
                "${ModuleDir}"
                "${GBT_ConfigIncludeDir}"
                ${ARG_INTERFACE_INCLUDES}
        )
    else()
        target_compile_features(${TargetName} PRIVATE cxx_std_23)
        target_link_libraries(${TargetName}
                PUBLIC ${ARG_PUBLIC_LINKS}
                PRIVATE ${ARG_PRIVATE_LINKS}
                INTERFACE ${ARG_INTERFACE_LINKS}
        )
        target_compile_definitions(${TargetName}
                PUBLIC ${ARG_PUBLIC_DEFINITIONS}
                PRIVATE ${ARG_PRIVATE_DEFINITIONS}
                INTERFACE ${ARG_INTERFACE_DEFINITIONS}
        )
        target_include_directories(${TargetName}
                PUBLIC "${ModuleDir}" "${GBT_ConfigIncludeDir}" ${ARG_PUBLIC_INCLUDES}
                PRIVATE ${ARG_PRIVATE_INCLUDES}
                INTERFACE ${ARG_INTERFACE_INCLUDES}
        )

        if(ARG_PCH)
            target_precompile_headers(${TargetName} PRIVATE "${ModuleDir}/${ARG_PCH}")
        endif()
    endif()

    message(STATUS "[GBT] module registered: ${PROJECT_NAME}::${ModuleName} [${Kind}]")
endfunction()

function(GBT_AddModuleFromDir ModuleName ModuleDir)
    GBT_AddModuleImpl("${ModuleName}" "${ModuleDir}" ${ARGN})
endfunction()

# =============================================================================
# GBT_AddModule(Name [KIND static|interface|executable] [options])
#
# Public entry point. Must be called from a GBTModule.cmake file. Library modules are
# static by default and may not be declared as shared/module libraries.
#
# Optional list arguments:
#   PUBLIC_LINKS / PRIVATE_LINKS / INTERFACE_LINKS
#   PUBLIC_DEFINITIONS / PRIVATE_DEFINITIONS / INTERFACE_DEFINITIONS
#   PUBLIC_INCLUDES / PRIVATE_INCLUDES / INTERFACE_INCLUDES
#   EXTERNAL_DEPENDENCIES (vcpkg ports added to the generated manifest)
#   GENERATED_SOURCES (absolute generated source paths added to the target)
#   PCH (file relative to the module directory)
#
# All supported source files beneath the module directory are collected recursively.
# The module directory is auto-added as PUBLIC, and generated GBTConfig.h is
# available from every module as <GBTConfig.h>.
# =============================================================================
macro(GBT_AddModule ModuleName)
    GBT_AddModuleImpl("${ModuleName}" "${CMAKE_CURRENT_LIST_DIR}" ${ARGN})
endmacro()
