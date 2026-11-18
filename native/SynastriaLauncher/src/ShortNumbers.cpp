#include <cstdio>
#include <cstring>
#include "Patch.h"
#include "ShortNumbers.h"

PrintfFunction g_shortNumbersPrintf = nullptr;

bool ShortenNumber(int value, bool heal, char* out, size_t size)
{
    bool shorten = value >= 10000;
    if (shorten)
    {
        double divisor = value < 999950 ? 1e3 : value < 999950000 ? 1e6 : 1e9;
        char suffix = value < 999950 ? 'K' : value < 999950000 ? 'M' : 'B';
        snprintf(out, size, heal ? "+%.1f%c" : "%.1f%c", value / divisor, suffix);
        char* dot = strchr(out, '.');
        if (dot != nullptr && dot[1] == '0')
        {
            dot[0] = dot[2];
            dot[1] = '\0';
        }
    }
    return shorten;
}

namespace
{
    int Print(char* buffer, uint32_t size, const char* format, int value, bool heal)
    {
        return ShortenNumber(value, heal, buffer, size)
            ? static_cast<int>(strlen(buffer))
            : g_shortNumbersPrintf(buffer, size, format, value);
    }
}

int __cdecl ShortDamage(char* buffer, uint32_t size, const char* format, int value)
{
    return Print(buffer, size, format, value, false);
}

int __cdecl ShortHeal(char* buffer, uint32_t size, const char* format, int value)
{
    return Print(buffer, size, format, value, true);
}

// Damage and healing numbers over targets are printed with the client's SStrPrintf and the formats "%d" (damage,
// 0x9E289C) and "+%d" (heal, 0xA33A4C) into a 32-byte buffer; those four calls come here instead.
Patch ShortNumbersPatch()
{
    auto printf = reinterpret_cast<void**>(&g_shortNumbersPrintf);
    auto damage = reinterpret_cast<const void*>(&ShortDamage);
    auto heal = reinterpret_cast<const void*>(&ShortHeal);
    return Patch
    {
        "ShortNumbers",
        {
            Code("DamageNumber", Hex("56 68 9C 28 9E 00 8D 45 E0 6A 20 50 E8 9C CC 04 00"), CallTo(12, damage, printf)),
            Code("DamageNumberOther", Hex("50 68 9C 28 9E 00 8D 45 E0 6A 20 50 E8 B7 F7 05 00"), CallTo(12, damage, printf)),
            Code("HealNumber", Hex("57 68 4C 3A A3 00 8D 55 E0 6A 20 52 E8 D7 CB 04 00"), CallTo(12, heal, printf)),
            Code("HealNumberOther", Hex("50 68 4C 3A A3 00 8D 45 E0 6A 20 50 E8 57 F7 05 00"), CallTo(12, heal, printf)),
        },
        [](const PatchConfig&) { g_shortNumbersPrintf = nullptr; },
    };
}
