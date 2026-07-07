#pragma once

#include "RefCounted.h"

#include <utility>

namespace GBT
{
    template <typename T>
    class RefPtr
    {
    public:
        RefPtr() = default;
        RefPtr(std::nullptr_t) {}

        explicit RefPtr(T* InPtr)
            : Ptr(InPtr)
        {
            AddRef();
        }

        RefPtr(const RefPtr& Other)
            : Ptr(Other.Ptr)
        {
            AddRef();
        }

        template <typename TOther>
        RefPtr(const RefPtr<TOther>& Other)
            : Ptr(Other.Get())
        {
            AddRef();
        }

        RefPtr(RefPtr&& Other) noexcept
            : Ptr(Other.Ptr)
        {
            Other.Ptr = nullptr;
        }

        ~RefPtr()
        {
            ReleaseRef();
        }

        RefPtr& operator=(const RefPtr& Other)
        {
            if (this == &Other)
            {
                return *this;
            }

            ReleaseRef();
            Ptr = Other.Ptr;
            AddRef();
            return *this;
        }

        RefPtr& operator=(RefPtr&& Other) noexcept
        {
            if (this == &Other)
            {
                return *this;
            }

            ReleaseRef();
            Ptr = Other.Ptr;
            Other.Ptr = nullptr;
            return *this;
        }

        T* Get() const { return Ptr; }
        T* operator->() const { return Ptr; }
        T& operator*() const { return *Ptr; }
        explicit operator bool() const { return Ptr != nullptr; }

        void Reset()
        {
            ReleaseRef();
            Ptr = nullptr;
        }

        bool IsValid() const { return operator bool(); }

    private:
        template <typename TOther>
        friend class RefPtr;

        void AddRef()
        {
            if (Ptr)
            {
                Ptr->AddRef();
            }
        }

        void ReleaseRef()
        {
            if (Ptr)
            {
                Ptr->ReleaseRef();
            }
        }

        T* Ptr = nullptr;
    };

    template <typename T, typename... TArgs>
    RefPtr<T> MakeRef(TArgs&&... Args)
    {
        return RefPtr<T>(new T(std::forward<TArgs>(Args)...));
    }
} // namespace GBT
