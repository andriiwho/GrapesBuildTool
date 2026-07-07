#include "ReflectionRegistry.h"

#include "Object.h"

namespace GBT
{
    namespace
    {
        const TypeInfo* FindBaseType(const TypeInfo& Type)
        {
            if (Type.BaseTypeName.empty())
            {
                return nullptr;
            }

            const String BaseTypeName = Type.BaseTypeName;
            const TypeInfo* BaseType = ReflectionRegistry::Get().FindType(BaseTypeName);
            if (!BaseType)
            {
                BaseType = ReflectionRegistry::Get().FindTypeByName(BaseTypeName);
            }

            return BaseType;
        }

        bool ContainsFieldNamed(const std::vector<const FieldInfo*>& Fields, StringView Name)
        {
            for (const FieldInfo* Field : Fields)
            {
                if (Field->Name == Name)
                {
                    return true;
                }
            }

            return false;
        }

        bool ContainsMethodNamed(const std::vector<const MethodInfo*>& Methods, StringView Name)
        {
            for (const MethodInfo* Method : Methods)
            {
                if (Method->Name == Name)
                {
                    return true;
                }
            }

            return false;
        }
    } // namespace

    const FieldInfo* TypeInfo::FindField(StringView Name) const
    {
        const TypeInfo* CurrentType = this;
        while (CurrentType)
        {
            for (const FieldInfo& Field : CurrentType->Fields)
            {
                if (Field.Name == Name)
                {
                    return &Field;
                }
            }

            CurrentType = FindBaseType(*CurrentType);
        }

        return nullptr;
    }

    const MethodInfo* TypeInfo::FindMethod(StringView Name) const
    {
        const TypeInfo* CurrentType = this;
        while (CurrentType)
        {
            for (const MethodInfo& Method : CurrentType->Methods)
            {
                if (Method.Name == Name)
                {
                    return &Method;
                }
            }

            CurrentType = FindBaseType(*CurrentType);
        }

        return nullptr;
    }

    std::vector<const FieldInfo*> TypeInfo::GetAllFields() const
    {
        std::vector<const FieldInfo*> Result;

        const TypeInfo* CurrentType = this;
        while (CurrentType)
        {
            for (const FieldInfo& Field : CurrentType->Fields)
            {
                if (!ContainsFieldNamed(Result, Field.Name))
                {
                    Result.push_back(&Field);
                }
            }

            CurrentType = FindBaseType(*CurrentType);
        }

        return Result;
    }

    std::vector<const MethodInfo*> TypeInfo::GetAllMethods() const
    {
        std::vector<const MethodInfo*> Result;

        const TypeInfo* CurrentType = this;
        while (CurrentType)
        {
            for (const MethodInfo& Method : CurrentType->Methods)
            {
                if (!ContainsMethodNamed(Result, Method.Name))
                {
                    Result.push_back(&Method);
                }
            }

            CurrentType = FindBaseType(*CurrentType);
        }

        return Result;
    }

    RefPtr<Object> TypeInfo::CreateInstance() const
    {
        return Factory ? Factory() : nullptr;
    }

    const EnumValueInfo* EnumInfo::FindValueByName(StringView Name) const
    {
        for (const EnumValueInfo& Value : Values)
        {
            if (Value.Name == Name)
            {
                return &Value;
            }
        }

        return nullptr;
    }

    const EnumValueInfo* EnumInfo::FindValueByValue(SInt64 Value) const
    {
        for (const EnumValueInfo& EnumValue : Values)
        {
            if (EnumValue.Value == Value)
            {
                return &EnumValue;
            }
        }

        return nullptr;
    }

    bool EnumInfo::TryFromString(StringView Name, SInt64& OutValue) const
    {
        const EnumValueInfo* Value = FindValueByName(Name);
        if (!Value)
        {
            return false;
        }

        OutValue = Value->Value;
        return true;
    }

    std::optional<SInt64> EnumInfo::FromString(StringView Name) const
    {
        SInt64 Value = 0;
        if (!TryFromString(Name, Value))
        {
            return std::nullopt;
        }

        return Value;
    }

    String EnumInfo::ToString(SInt64 Value) const
    {
        const EnumValueInfo* EnumValue = FindValueByValue(Value);
        return EnumValue ? EnumValue->Name : String();
    }

    ReflectionRegistry& ReflectionRegistry::Get()
    {
        static ReflectionRegistry Registry;
        return Registry;
    }

    void ReflectionRegistry::RegisterType(TypeInfo Info)
    {
        auto Existing = TypeIndices.find(Info.QualifiedName);
        if (Existing != TypeIndices.end())
        {
            Types[Existing->second] = std::move(Info);
            return;
        }

        TypeIndices.emplace(Info.QualifiedName, Types.size());
        Types.emplace_back(std::move(Info));
    }

    void ReflectionRegistry::RegisterEnum(EnumInfo Info)
    {
        auto Existing = EnumIndices.find(Info.QualifiedName);
        if (Existing != EnumIndices.end())
        {
            EnumTypeIndices[Info.TypeId] = Existing->second;
            Enums[Existing->second] = std::move(Info);
            return;
        }

        EnumIndices.emplace(Info.QualifiedName, Enums.size());
        EnumTypeIndices.emplace(Info.TypeId, Enums.size());
        Enums.emplace_back(std::move(Info));
    }

    const TypeInfo* ReflectionRegistry::FindType(StringView QualifiedName) const
    {
        auto It = TypeIndices.find(String(QualifiedName));
        if (It == TypeIndices.end())
        {
            return nullptr;
        }

        return &Types[It->second];
    }

    const TypeInfo* ReflectionRegistry::FindTypeByName(StringView Name) const
    {
        for (const TypeInfo& Type : Types)
        {
            if (Type.Name == Name)
            {
                return &Type;
            }
        }

        return nullptr;
    }

    const EnumInfo* ReflectionRegistry::FindEnum(StringView QualifiedName) const
    {
        auto It = EnumIndices.find(String(QualifiedName));
        if (It == EnumIndices.end())
        {
            return nullptr;
        }

        return &Enums[It->second];
    }

    const EnumInfo* ReflectionRegistry::FindEnumByName(StringView Name) const
    {
        for (const EnumInfo& Enum : Enums)
        {
            if (Enum.Name == Name)
            {
                return &Enum;
            }
        }

        return nullptr;
    }

    const EnumInfo* ReflectionRegistry::FindEnum(std::type_index TypeId) const
    {
        auto It = EnumTypeIndices.find(TypeId);
        if (It == EnumTypeIndices.end())
        {
            return nullptr;
        }

        return &Enums[It->second];
    }

    bool ReflectionRegistry::IsA(const TypeInfo* Type, const TypeInfo* Base) const
    {
        if (!Type || !Base)
        {
            return false;
        }

        const TypeInfo* CurrentType = Type;
        while (CurrentType)
        {
            if (CurrentType == Base || CurrentType->QualifiedName == Base->QualifiedName)
            {
                return true;
            }

            if (CurrentType->BaseTypeName.empty())
            {
                return false;
            }

            if (CurrentType->BaseTypeName == Base->QualifiedName || CurrentType->BaseTypeName == Base->Name)
            {
                return true;
            }

            const String BaseTypeName = CurrentType->BaseTypeName;

            CurrentType = FindType(BaseTypeName);
            if (!CurrentType)
            {
                CurrentType = FindTypeByName(BaseTypeName);
            }
        }

        return false;
    }

    bool ReflectionRegistry::IsA(StringView QualifiedName, StringView BaseQualifiedName) const
    {
        return IsA(FindType(QualifiedName), FindType(BaseQualifiedName));
    }
} // namespace GBT
