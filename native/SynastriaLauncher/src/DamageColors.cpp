#include "DamageColors.h"
#include "Patch.h"
#include "Settings.h"

void* g_createDamageText = nullptr;

namespace
{
    uint32_t g_amounts[MaxDamageTiers] = {};
    uint32_t g_colors[MaxDamageTiers] = {};
    size_t g_count = 0;
}

void SetDamageTiers(const std::vector<DamageTier>& tiers)
{
    g_count = tiers.size() < MaxDamageTiers ? tiers.size() : MaxDamageTiers;
    for (size_t i = 0; i < g_count; ++i)
    {
        g_amounts[i] = tiers[i].amount;
        g_colors[i] = 0xFF000000u | tiers[i].rgb;
    }
}

extern "C" const uint32_t* __cdecl PickDamageColor(int amount)
{
    for (size_t i = 0; i < g_count; ++i)
    {
        if (amount >= static_cast<int>(g_amounts[i]))
        {
            return &g_colors[i];
        }
    }
    return nullptr;
}

// On entry [esp+16] is the color pointer argument; eax, ecx and edx are free, as at the original call.
extern "C" __declspec(naked) void DamageTextFromSecondArg()
{
    __asm
    {
        mov eax, dword ptr [ebp + 0Ch]
        push eax
        call PickDamageColor
        add esp, 4
        test eax, eax
        jz keep
        mov dword ptr [esp + 10h], eax
    keep:
        jmp dword ptr [g_createDamageText]
    }
}

extern "C" __declspec(naked) void DamageTextFromFirstArg()
{
    __asm
    {
        mov eax, dword ptr [ebp + 8]
        push eax
        call PickDamageColor
        add esp, 4
        test eax, eax
        jz keep
        mov dword ptr [esp + 10h], eax
    keep:
        jmp dword ptr [g_createDamageText]
    }
}

// The two calls that create damage text over targets; the function they call takes the text's color by pointer.
Patch DamageColorsPatch()
{
    return Patch
    {
        "DamageColors",
        {
            Code("DamageText", Hex("83 E1 02 51 52 E8 03 3C 0C 00"),
                CallTo(5, reinterpret_cast<const void*>(&DamageTextFromSecondArg), &g_createDamageText)),
            Code("DamageTextOther", Hex("51 6A 08 52 E8 5E 67 0D 00"),
                CallTo(4, reinterpret_cast<const void*>(&DamageTextFromFirstArg), &g_createDamageText)),
        },
        [](const PatchConfig& config)
        {
            g_createDamageText = nullptr;
            SetDamageTiers(config.damageColors);
        },
    };
}
