#include "Object.h"

#include "ReflectionRegistry.h"

namespace GBT
{
    const TypeInfo* Object::GetStaticType()
    {
        return ReflectionRegistry::Get().FindType("GBT::Object");
    }

    const TypeInfo* Object::GetTypeInfo() const
    {
        return GetType();
    }

    const TypeInfo* Object::GetType() const
    {
        return GetStaticType();
    }
} // namespace GBT
