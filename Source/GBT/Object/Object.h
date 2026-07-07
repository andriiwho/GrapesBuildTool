#pragma once

#include "Core/RefCounted.h"

namespace GBT
{
    class Object : public RefCounted
    {
    public:
        using This = Object;
        using Base = RefCounted;

        static const class TypeInfo* GetStaticType();
        virtual const class TypeInfo* GetTypeInfo() const;
        virtual const class TypeInfo* GetType() const;

    protected:
        ~Object() override = default;
    };
} // namespace GBT
