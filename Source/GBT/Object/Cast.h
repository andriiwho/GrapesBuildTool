#pragma once

#include "Object.h"
#include "ReflectionRegistry.h"

#include <cassert>
#include <type_traits>

namespace GBT
{
    template <typename T>
    T* Cast(Object* Instance)
    {
        static_assert(std::is_base_of_v<Object, T>, "Cast<T> requires T to inherit from GBT::Object");

        if (!Instance)
        {
            return nullptr;
        }

        if constexpr (std::is_same_v<T, Object>)
        {
            return Instance;
        }

        const TypeInfo* SourceType = Instance->GetType();
        const TypeInfo* TargetType = T::GetStaticType();
        if (!SourceType || !TargetType)
        {
            return nullptr;
        }

        return ReflectionRegistry::Get().IsA(SourceType, TargetType) ? static_cast<T*>(Instance) : nullptr;
    }

    template <typename T>
    const T* Cast(const Object* Instance)
    {
        static_assert(std::is_base_of_v<Object, T>, "Cast<T> requires T to inherit from GBT::Object");

        if (!Instance)
        {
            return nullptr;
        }

        if constexpr (std::is_same_v<T, Object>)
        {
            return Instance;
        }

        const TypeInfo* SourceType = Instance->GetType();
        const TypeInfo* TargetType = T::GetStaticType();
        if (!SourceType || !TargetType)
        {
            return nullptr;
        }

        return ReflectionRegistry::Get().IsA(SourceType, TargetType) ? static_cast<const T*>(Instance) : nullptr;
    }

    template <typename T>
    bool IsA(const Object* Instance)
    {
        return Cast<T>(Instance) != nullptr;
    }

    template <typename T>
    T& CastChecked(Object& Instance)
    {
        T* CastedInstance = Cast<T>(&Instance);
        assert(CastedInstance && "CastChecked<T> failed");
        return *CastedInstance;
    }

    template <typename T>
    const T& CastChecked(const Object& Instance)
    {
        const T* CastedInstance = Cast<T>(&Instance);
        assert(CastedInstance && "CastChecked<T> failed");
        return *CastedInstance;
    }

    template <typename T, typename TObject>
    RefPtr<T> Cast(const RefPtr<TObject>& Instance)
    {
        static_assert(std::is_base_of_v<Object, T>, "Cast<T> requires T to inherit from GBT::Object");
        static_assert(std::is_base_of_v<Object, TObject>, "Cast<T> requires an object RefPtr");

        return RefPtr<T>(Cast<T>(Instance.Get()));
    }
} // namespace GBT
