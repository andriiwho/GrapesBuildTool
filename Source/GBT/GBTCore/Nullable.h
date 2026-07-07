#pragma once

#include <optional>
#include <functional>
#include <type_traits>
#include <utility>

#include "Types.h"

namespace GBT
{
    struct NoneTag
    {
    };

    inline constexpr NoneTag None;

    template <typename T>
    struct Some
    {
        T Value;

        Some() = delete;

        explicit Some(const T& InValue)
            : Value(InValue) {}

        explicit Some(T&& InValue) noexcept
            : Value(std::move(InValue)) {}
    };

    template <typename T>
    class Nullable
    {
    public:
        Nullable()
            : Value(std::nullopt)
        {
        }

        Nullable(NoneTag)
            : Value(std::nullopt)
        {
        }

        Nullable(const Some<T>& InValue)
            : Value(InValue.Value)
        {
        }

        Nullable& operator=(const Some<T>& InValue)
        {
            Value = InValue.Value;
            return *this;
        }

        Nullable& operator=(const Nullable& InNullable) = default;
        Nullable(const Nullable& InNullable) = default;

        Nullable(Some<T>&& InValue) noexcept
            : Value(std::move(InValue.Value))
        {
        }

        Nullable(Nullable&& InNullable) noexcept
            : Value(std::move(InNullable.Value))
        {
        }

        Nullable& operator=(Nullable&& InNullable) noexcept
        {
            if (&InNullable == this)
            {
                return *this;
            }

            Value = std::move(InNullable.Value);
            return *this;
        }

        T& operator*() { return *Value; }
        const T& operator*() const { return *Value; }

        T* operator->() { return &Value.value(); }
        const T* operator->() const { return &Value.value(); }

        operator bool() const { return Value.has_value(); }
        bool IsValid() const { return Value.has_value(); }

        template <typename... TArgs>
        void Emplace(TArgs&&... Args)
        {
            Value.emplace(std::forward<TArgs>(Args)...);
        }

        T& Get() { return *Value; }
        const T& Get() const { return *Value; }

        T Expect(StringView message)
        {
            ValidateMsgf(IsValid(), "Nullable was null: {}", message);
            return Value.value();
        }

        template <typename TCallable>
            requires std::invocable<TCallable&&, T&>
            && (!std::is_void_v<std::invoke_result_t<TCallable &&, T&>>)
        auto Transform(TCallable&& Callable)
        {
            using TResult = std::remove_cvref_t<std::invoke_result_t<TCallable&&, T&>>;

            if (!IsValid())
            {
                return Nullable<TResult>(None);
            }

            return Nullable<TResult>(
                Some<TResult>(std::invoke(std::forward<TCallable>(Callable), Get())));
        }

        template <typename TCallable>
            requires std::invocable<TCallable&&, const T&>
            && (!std::is_void_v<std::invoke_result_t<TCallable &&, const T&>>)
        auto Transform(TCallable&& Callable) const
        {
            using TResult = std::remove_cvref_t<std::invoke_result_t<TCallable&&, const T&>>;

            if (!IsValid())
            {
                return Nullable<TResult>(None);
            }

            return Nullable<TResult>(
                Some<TResult>(std::invoke(std::forward<TCallable>(Callable), Get())));
        }

        template <typename TCallable>
            requires std::invocable<TCallable&&, T&>
            && (!std::is_void_v<std::invoke_result_t<TCallable &&, T&>>)
        auto AndThen(TCallable&& Callable);

        template <typename TCallable>
            requires std::invocable<TCallable&&, const T&>
            && (!std::is_void_v<std::invoke_result_t<TCallable &&, const T&>>)
        auto AndThen(TCallable&& Callable) const;

        template <typename TCallable>
            requires std::invocable<TCallable&&>
            && (!std::is_void_v<std::invoke_result_t<TCallable &&>>)
        Nullable OrElse(TCallable&& Callable) const;

    private:
        std::optional<T> Value;
    };

    template <typename T>
    struct IsNullable : std::false_type
    {
    };

    template <typename T>
    struct IsNullable<Nullable<T>> : std::true_type
    {
    };

    template <typename T>
    inline constexpr bool IsNullableV = IsNullable<T>::value;

    template <typename T>
    concept CNullable = IsNullableV<T>;

    template <typename T>
    template <typename TCallable>
        requires std::invocable<TCallable&&, T&>
        && (!std::is_void_v<std::invoke_result_t<TCallable &&, T&>>)
    auto Nullable<T>::AndThen(TCallable&& Callable)
    {
        using TResult = std::invoke_result_t<TCallable&&, Nullable&>;
        static_assert(IsNullableV<TResult>, "AndThen must callable must return Nullable<U>");

        if (IsValid())
        {
            return std::invoke(std::forward<TCallable>(Callable), Get());
        }
        return TResult(None);
    }

    template <typename T>
    template <typename TCallable>
        requires std::invocable<TCallable&&, const T&> && (!std::is_void_v<std::invoke_result_t<TCallable &&, const T&>>)
    auto Nullable<T>::AndThen(TCallable&& Callable) const
    {
        using TResult = std::invoke_result_t<TCallable&&, Nullable&>;
        static_assert(IsNullableV<TResult>, "AndThen must callable must return Nullable<U>");

        if (IsValid())
        {
            return std::invoke(std::forward<TCallable>(Callable), Get());
        }
        return TResult(None);
    }

    template <typename T>
    template <typename TCallable>
        requires std::invocable<TCallable&&> && (!std::is_void_v<std::invoke_result_t<TCallable &&>>)
    Nullable<T> Nullable<T>::OrElse(TCallable&& Callable) const
    {
        using TResult = std::invoke_result_t<TCallable&&>;
        static_assert(std::same_as<std::remove_cvref_t<TResult>, Nullable>, "OrElse callable must return Nullable<T>");

        if (IsValid())
        {
            return *this;
        }

        return std::invoke(std::forward<TCallable>(Callable));
    }

} // namespace GBT