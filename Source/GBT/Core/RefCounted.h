#pragma once

#include "Types.h"

#include <atomic>

namespace GBT
{
    class RefCounted
    {
    public:
        RefCounted() = default;
        RefCounted(const RefCounted&) = delete;
        RefCounted& operator=(const RefCounted&) = delete;

        void AddRef() const
        {
            RefCount.fetch_add(1, std::memory_order_relaxed);
        }

        void ReleaseRef() const
        {
            if (RefCount.fetch_sub(1, std::memory_order_acq_rel) == 1)
            {
                delete this;
            }
        }

        UInt32 GetRefCount() const
        {
            return RefCount.load(std::memory_order_acquire);
        }

    protected:
        virtual ~RefCounted() = default;

    private:
        mutable std::atomic<UInt32> RefCount{0};
    };
} // namespace GBT
