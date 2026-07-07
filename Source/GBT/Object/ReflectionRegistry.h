#pragma once

#include "GBTCore/RefPtr.h"
#include "GBTCore/Types.h"

#include <functional>
#include <optional>
#include <span>
#include <typeindex>
#include <type_traits>
#include <unordered_map>
#include <utility>
#include <vector>

namespace GBT
{
    class Object;

    enum class TypeDeclarationKind
    {
        Class,
        Struct
    };

    enum class TypeFlags : UInt32
    {
        None = 0,
        Abstract = 1 << 0,
        Object = 1 << 1,
        ValueType = 1 << 2
    };

    enum class FieldFlags : UInt32
    {
        None = 0,
        ReadOnly = 1 << 0,
        ReadWrite = 1 << 1
    };

    enum class MethodFlags : UInt32
    {
        None = 0,
        Const = 1 << 0,
        Static = 1 << 1
    };

    struct MetadataEntry
    {
        String Key;
        String Value;
    };

    struct ParameterInfo
    {
        String Name;
        String TypeName;
    };

    using FieldAddressGetter = std::function<const void*(const void*)>;
    using FieldMutableAddressGetter = std::function<void*(void*)>;
    using MethodInvoker = std::function<void(void*, void*, std::span<void*>)>;
    using ObjectFactory = std::function<RefPtr<Object>()>;

    struct EnumValueInfo
    {
        String Name;
        SInt64 Value = 0;
        std::vector<MetadataEntry> Metadata;
    };

    template <typename T>
    decltype(auto) ReadMethodArgument(void* Argument)
    {
        return *static_cast<std::remove_reference_t<T>*>(Argument);
    }

    template <typename T>
    void WriteMethodReturn(void* ReturnValue, T&& Value)
    {
        using TValue = std::remove_cvref_t<T>;
        if (ReturnValue)
        {
            *static_cast<TValue*>(ReturnValue) = std::forward<T>(Value);
        }
    }

    struct FieldInfo
    {
        String Name;
        String TypeName;
        FieldFlags Flags = FieldFlags::None;
        std::vector<MetadataEntry> Metadata;
        FieldAddressGetter AddressGetter;
        FieldMutableAddressGetter MutableAddressGetter;

        const void* GetAddress(const void* Instance) const
        {
            return AddressGetter ? AddressGetter(Instance) : nullptr;
        }

        void* GetMutableAddress(void* Instance) const
        {
            return MutableAddressGetter ? MutableAddressGetter(Instance) : nullptr;
        }

        template <typename T>
        const T* GetValue(const void* Instance) const
        {
            return static_cast<const T*>(GetAddress(Instance));
        }

        template <typename T>
        T* GetMutableValue(void* Instance) const
        {
            return static_cast<T*>(GetMutableAddress(Instance));
        }

        bool CanRead() const { return static_cast<bool>(AddressGetter); }
        bool CanWrite() const { return static_cast<bool>(MutableAddressGetter); }
    };

    struct MethodInfo
    {
        String Name;
        String NativeName;
        String ReturnTypeName;
        MethodFlags Flags = MethodFlags::None;
        std::vector<ParameterInfo> Parameters;
        std::vector<MetadataEntry> Metadata;
        MethodInvoker Invoker;

        bool CanInvoke() const { return static_cast<bool>(Invoker); }

        void Invoke(void* Instance, void* ReturnValue, std::span<void*> Arguments) const
        {
            if (Invoker)
            {
                Invoker(Instance, ReturnValue, Arguments);
            }
        }
    };

    struct TypeInfo
    {
        String Name;
        String QualifiedName;
        String ModuleName;
        String SourceFile;
        TypeDeclarationKind DeclarationKind = TypeDeclarationKind::Class;
        TypeFlags Flags = TypeFlags::None;
        String BaseTypeName;
        std::vector<MetadataEntry> Metadata;
        std::vector<FieldInfo> Fields;
        std::vector<MethodInfo> Methods;
        ObjectFactory Factory;

        const FieldInfo* FindField(StringView Name) const;
        const MethodInfo* FindMethod(StringView Name) const;
        std::vector<const FieldInfo*> GetAllFields() const;
        std::vector<const MethodInfo*> GetAllMethods() const;
        bool CanCreateInstance() const { return static_cast<bool>(Factory); }
        RefPtr<Object> CreateInstance() const;
    };

    struct EnumInfo
    {
        String Name;
        String QualifiedName;
        String ModuleName;
        String SourceFile;
        String UnderlyingTypeName;
        std::type_index TypeId = typeid(void);
        std::vector<MetadataEntry> Metadata;
        std::vector<EnumValueInfo> Values;

        const EnumValueInfo* FindValueByName(StringView Name) const;
        const EnumValueInfo* FindValueByValue(SInt64 Value) const;
        const std::vector<EnumValueInfo>& GetValues() const { return Values; }
        bool TryFromString(StringView Name, SInt64& OutValue) const;
        std::optional<SInt64> FromString(StringView Name) const;
        String ToString(SInt64 Value) const;

        template <typename T>
        bool TryFromString(StringView Name, T& OutValue) const
        {
            SInt64 Value = 0;
            if (!TryFromString(Name, Value))
            {
                return false;
            }

            OutValue = static_cast<T>(Value);
            return true;
        }

        template <typename T>
        String ToString(T Value) const
        {
            return ToString(static_cast<SInt64>(Value));
        }
    };

    class ReflectionRegistry
    {
    public:
        static ReflectionRegistry& Get();

        void RegisterType(TypeInfo Info);
        void RegisterEnum(EnumInfo Info);
        const TypeInfo* FindType(StringView QualifiedName) const;
        const TypeInfo* FindTypeByName(StringView Name) const;
        const EnumInfo* FindEnum(StringView QualifiedName) const;
        const EnumInfo* FindEnumByName(StringView Name) const;
        const EnumInfo* FindEnum(std::type_index TypeId) const;
        template <typename T>
        const EnumInfo* FindEnum() const
        {
            static_assert(std::is_enum_v<T>, "FindEnum<T>() requires an enum type.");
            return FindEnum(std::type_index(typeid(T)));
        }
        bool IsA(const TypeInfo* Type, const TypeInfo* Base) const;
        bool IsA(StringView QualifiedName, StringView BaseQualifiedName) const;
        const std::vector<TypeInfo>& GetTypes() const { return Types; }
        const std::vector<EnumInfo>& GetEnums() const { return Enums; }

    private:
        std::vector<TypeInfo> Types;
        std::vector<EnumInfo> Enums;
        std::unordered_map<String, USize> TypeIndices;
        std::unordered_map<String, USize> EnumIndices;
        std::unordered_map<std::type_index, USize> EnumTypeIndices;
    };

    template <typename T>
    const EnumInfo* GetEnumInfo()
    {
        static_assert(std::is_enum_v<T>, "GetEnumInfo<T>() requires an enum type.");
        return ReflectionRegistry::Get().FindEnum<T>();
    }

    template <typename T>
    const std::vector<EnumValueInfo>& GetEnumValues()
    {
        static_assert(std::is_enum_v<T>, "GetEnumValues<T>() requires an enum type.");
        static const std::vector<EnumValueInfo> EmptyValues;
        const EnumInfo* Info = GetEnumInfo<T>();
        return Info ? Info->GetValues() : EmptyValues;
    }

    template <typename T>
    String EnumToString(T Value)
    {
        static_assert(std::is_enum_v<T>, "EnumToString<T>() requires an enum type.");
        const EnumInfo* Info = GetEnumInfo<T>();
        return Info ? Info->ToString(Value) : String();
    }

    template <typename T>
    bool TryStringToEnum(StringView Name, T& OutValue)
    {
        static_assert(std::is_enum_v<T>, "TryStringToEnum<T>() requires an enum type.");
        const EnumInfo* Info = GetEnumInfo<T>();
        return Info ? Info->TryFromString(Name, OutValue) : false;
    }

    template <typename T>
    std::optional<T> StringToEnum(StringView Name)
    {
        static_assert(std::is_enum_v<T>, "StringToEnum<T>() requires an enum type.");
        T Value{};
        if (!TryStringToEnum(Name, Value))
        {
            return std::nullopt;
        }

        return Value;
    }

    void RegisterGeneratedReflectionTypes();
} // namespace GBT
