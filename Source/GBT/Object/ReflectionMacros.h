#pragma once

#include "ReflectionRegistry.h"

#define GBT_Type(...)
#define GBT_Enum(...)
#define GBT_EnumValue(...)
#define GBT_Field(...)
#define GBT_Method(...)

#define GBT_JOIN_INNER(A, B) A##B
#define GBT_JOIN(A, B) GBT_JOIN_INNER(A, B)
#define GBT_TYPE_METADATA(Line) GBT_JOIN(GBT_TYPE_METADATA_LINE_, Line)
#define GBT_TypeMetadata() GBT_TYPE_METADATA(__LINE__)
