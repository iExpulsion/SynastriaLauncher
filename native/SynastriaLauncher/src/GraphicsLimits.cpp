// Graphics Limits, from St0ny's AIO WoW EXE Patcher: raises the view distance, horizon, environment detail and
// ground clutter limits, and the video menu sliders to match.
#include <cstring>
#include "Client.h"
#include "GraphicsLimits.h"
#include "Patch.h"

namespace
{
    struct SliderLimit
    {
        const char* cvar;
        float maximum;
    };

    const SliderLimit g_sliderLimits[] =
    {
        { "farclip", 2477.0f },
        { "environmentDetail", 2.5f },
        { "groundEffectDist", 250.0f },
        { "groundEffectDensity", 256.0f },
    };

    // The farclip setting is clamped between these two; both are raised so the slider's range can be used.
    constexpr float FarclipLowStock = 2375.0f / 3.0f;
    constexpr float FarclipHighStock = 4750.0f / 3.0f;
    constexpr float FarclipRaised = 10000.0f;

    constexpr float GroundEffectDistStock = 140.0f;
    constexpr float GroundEffectDistRaised = 9500.0f / 3.0f;

    // The client's slider maximum lookup: compare the setting name with "farclip" (SStrCmpI, then branch to the "no
    // maximum" path when it differs) and load farclip's maximum as a double. It is replaced, in the same 26 bytes,
    // with a call to SliderMaximum(name) and the same two outcomes. The name is in esi.
    const Bytes FarclipCompare = Hex("68 FF FF FF 7F 68") + U32(Client::FarclipName) + Hex("56");

    constexpr size_t LookupLength = 26;
    constexpr size_t MissBranch = 0x12;   // the 2-byte jne to the "no maximum" path

    std::string LookupStub(uint8_t* at, Edit& edit)
    {
        uint8_t* miss = at + MissBranch + 2 + static_cast<int8_t>(at[MissBranch + 1]);
        Bytes code = Hex("56");                                          // push esi
        uint8_t* call = at + code.size();
        uint32_t rel = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(&SliderMaximum) - reinterpret_cast<uintptr_t>(call + 5));
        code = code + Hex("E8") + U32(rel);                              // call SliderMaximum
        code = code + Hex("83 C4 04");                                   // add esp, 4
        code = code + Hex("85 C0");                                      // test eax, eax
        intptr_t toMiss = miss - (at + code.size() + 2);
        code = code + Hex("74") + Bytes { static_cast<uint8_t>(toMiss) }; // jz miss
        code = code + Hex("D9 00");                                      // fld dword ptr [eax]
        code = code + Bytes(LookupLength - code.size(), 0x90);
        edit.offset = 0;
        edit.bytes = code;
        return toMiss >= -128 && toMiss <= 127 ? "" : "the no-maximum path is out of reach";
    }
}

extern "C" const float* __cdecl SliderMaximum(const char* cvarName)
{
    for (const SliderLimit& limit : g_sliderLimits)
    {
        if (cvarName != nullptr && _stricmp(cvarName, limit.cvar) == 0)
        {
            return &limit.maximum;
        }
    }
    return nullptr;
}

Patch GraphicsLimitsPatch()
{
    const Bytes compareTail = Hex("85 C0 75 20 DD 05 D8 57 9F 00");
    return Patch
    {
        "GraphicsLimits",
        {
            Swap("FarclipClamp",
                Hex("37 43") + F32(FarclipLowStock) + F32(FarclipHighStock) + Hex("90 C2"),
                Hex("37 43") + F32(FarclipRaised) + F32(FarclipRaised) + Hex("90 C2")),
            // The horizon setting is clamped to a constant; it reads the client's 12.0 instead.
            Swap("HorizonLimit",
                Hex("DD D8 D9 05") + U32(Client::HorizonScaleLimit) + Hex("D8 D1 DF E0"),
                Hex("DD D8 D9 05") + U32(Client::FloatTwelve) + Hex("D8 D1 DF E0")),
            // The environment detail setter stores the clamped value (fstp st(1)); it keeps the raw one (fstp st(0)).
            Swap("EnvironmentDetailClamp",
                Hex("05 7A 12 51 DD D9 D9 1C 24 E8 D9 18"),
                Hex("05 7A 12 51 DD D8 D9 1C 24 E8 D9 18")),
            Swap("GroundEffectDistLimit",
                F32(6.0f) + F32(GroundEffectDistStock) + F32(-2.0f),
                F32(6.0f) + F32(GroundEffectDistRaised) + F32(-2.0f)),
            Code("GlueSliderMaximum", FarclipCompare + Hex("E8 2A 07 29 00") + compareTail, LookupStub),
            Code("GameSliderMaximum", FarclipCompare + Hex("E8 86 98 25 00") + compareTail, LookupStub),
        },
        [](const PatchConfig&) {},
    };
}
