#pragma once

#include <cstdint>
#include <stdexcept>

using UInt8 = uint8_t;
using UInt16 = uint16_t;
using UInt32 = uint32_t;
using UInt64 = uint64_t;

using SInt8 = int8_t;
using SInt16 = int16_t;
using SInt32 = int32_t;
using SInt64 = int64_t;

#include <cstddef>

using USize = std::size_t;
using SSize = std::ptrdiff_t;

using Char = char;
using WideChar = wchar_t;

using SChar = char;
using Short = short;
using SInt = int;
using SLong = long long;

using UShort = unsigned short;
using UInt = unsigned int;
using ULong = unsigned long long;
using UChar = unsigned char;

#include <string>
#include <string_view>

// Container types
using String = std::string;
using StringView = std::string_view;

using WideString = std::wstring;
using WideStringView = std::wstring_view;

// Pointer types

#include <memory>

namespace GBT
{
    enum class Byte : UInt8
    {
    };

    template <typename T, typename Deleter = std::default_delete<T>>
    using OwnedPtr = std::unique_ptr<T, Deleter>;

    template <typename T>
    using SharedPtr = std::shared_ptr<T>;

    template <typename T>
    using WeakPtr = std::weak_ptr<T>;

    template <typename T, typename... TArgs>
    OwnedPtr<T> MakeOwned(TArgs&&... InArgs)
    {
        return std::make_unique<T>(std::forward<TArgs>(InArgs)...);
    }

    template <typename T, typename TDeleter = std::default_delete<T>, typename... TArgs>
    OwnedPtr<T, TDeleter> MakeOwnedWithDeleter(TArgs&&... InArgs)
    {
        return OwnedPtr<T, TDeleter>(new T(std::forward<TArgs>(InArgs)...));
    }

    template <typename T, typename... TArgs>
    SharedPtr<T> MakeShared(TArgs&&... InArgs)
    {
        return std::make_shared<T>(std::forward<TArgs>(InArgs)...);
    }

    template <typename T, typename TAlloc = std::allocator<T>, typename... TArgs>
    SharedPtr<T> MakeSharedWithAllocator(const TAlloc& Alloc, TArgs&&... Args)
    {
        return std::allocate_shared<T>(Alloc, std::forward<TArgs>(Args)...);
    }
} // namespace GBT

// The initializer patter used throughout the engine
namespace GBT
{
    template <typename TContained>
    class InitializerBase
    {
    public:
        InitializerBase() = default;
        virtual ~InitializerBase() = default;

        inline TContained* operator->() const { return Contained.get(); }

    protected:
        OwnedPtr<TContained> Contained;
    };
} // namespace GBT
