#pragma once

#include "GBTCore/Types.h"

namespace GBT
{
    // Result returned by the dependency-free reflection runtime validation suite.
    // Test executables consume the message when a metadata invariant fails.
    struct ReflectionValidationResult
    {
        bool Passed = false;
        String Message;
    };

    // Exercises runtime value classification, sequence operations, registry
    // ambiguity handling, and generated/runtime ABI rejection without relying
    // on a particular client project's reflected types.
    ReflectionValidationResult RunReflectionValidationSuite();
} // namespace GBT
