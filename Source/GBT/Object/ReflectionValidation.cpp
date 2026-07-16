#include "ReflectionValidation.h"

#include "ReflectionRegistry.h"

#include <stdexcept>
#include <vector>

namespace GBT
{
    namespace ReflectionValidationInternal
    {
        ReflectionValidationResult Fail(String Message)
        {
            return { .Passed = false, .Message = std::move(Message) };
        }
    } // namespace ReflectionValidationInternal

    ReflectionValidationResult RunReflectionValidationSuite()
    {
        std::vector<SInt32> Values = { 4, 8 };
        ValueTypeInfo Sequence = MakeValueTypeInfo<decltype(Values)>();
        if (Sequence.Kind != ValueKind::Sequence || Sequence.Sequence.ElementKind != ValueKind::Primitive)
            return ReflectionValidationInternal::Fail("std::vector<SInt32> was not classified as a primitive sequence.");
        if (!Sequence.Sequence.Size || Sequence.Sequence.Size(&Values) != 2 || !Sequence.Sequence.Resize)
            return ReflectionValidationInternal::Fail("Sequence size/resize operations were not generated.");
        Sequence.Sequence.Resize(&Values, 3);
        if (Values.size() != 3 || !Sequence.Sequence.MutableElementAddress)
            return ReflectionValidationInternal::Fail("Sequence resize or mutable element access failed.");
        *static_cast<SInt32*>(Sequence.Sequence.MutableElementAddress(&Values, 2)) = 12;
        if (Values[2] != 12)
            return ReflectionValidationInternal::Fail("Sequence mutable element address did not reference storage.");

        ValueTypeInfo BooleanSequence = MakeValueTypeInfo<std::vector<bool>>();
        if (BooleanSequence.Sequence.ElementAddress || BooleanSequence.Sequence.MutableElementAddress)
            return ReflectionValidationInternal::Fail("std::vector<bool> incorrectly exposed proxy elements as addresses.");

        ReflectionRegistry Registry;
        TypeInfo First;
        First.Name = "Item";
        First.QualifiedName = "First::Item";
        Registry.RegisterType(std::move(First));
        TypeInfo Second;
        Second.Name = "Item";
        Second.QualifiedName = "Second::Item";
        Registry.RegisterType(std::move(Second));
        if (!Registry.FindType("First::Item") || !Registry.FindType("Second::Item"))
            return ReflectionValidationInternal::Fail("Qualified lookup failed for duplicate unqualified names.");
        if (Registry.FindTypeByName("Item"))
            return ReflectionValidationInternal::Fail("Ambiguous unqualified type lookup did not fail.");

        bool RejectedMismatchedAbi = false;
        try
        {
            TypeInfo Stale;
            Stale.AbiVersion = ReflectionAbiVersion + 1;
            Stale.Name = "Stale";
            Stale.QualifiedName = "Stale";
            Registry.RegisterType(std::move(Stale));
        }
        catch (const std::invalid_argument&)
        {
            RejectedMismatchedAbi = true;
        }
        if (!RejectedMismatchedAbi)
            return ReflectionValidationInternal::Fail("Mismatched reflection ABI metadata was accepted.");

        return { .Passed = true };
    }
} // namespace GBT
