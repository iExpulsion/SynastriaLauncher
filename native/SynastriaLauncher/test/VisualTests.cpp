// Unit tests for Graphics Limits and HD Portraits: the slider maximum table, the generated slider lookup code run on a
// copy of the client's code shape, the render target and texture size shims, and the portrait mask and its lookup.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include <string>
#include "../src/GraphicsLimits.h"
#include "../src/HdPortraits.h"
#include "../src/Patch.h"
#include "NativeTests.h"

#pragma warning(disable : 4731)

namespace
{
    std::string Maximum(const char* cvar)
    {
        const float* value = SliderMaximum(cvar);
        return value != nullptr ? std::to_string(*value) : "none";
    }

    const Site& FindSite(const char* patchId, const char* siteName)
    {
        const Site* found = nullptr;
        for (const Patch& patch : Patches())
        {
            for (const Site& site : patch.sites)
            {
                found = strcmp(patch.id, patchId) == 0 && strcmp(site.name, siteName) == 0 ? &site : found;
            }
        }
        return *found;
    }

    float s_sliderValue = 0;
    uint32_t s_path = 0;
    uint32_t s_ebxAfter = 0;

    // Lays the client's slider lookup out in executable memory, with a "found" path after its 26 bytes that stores
    // the loaded maximum and a "no maximum" path where its branch goes, patches it, and runs it with esi = name.
    uint32_t RunSliderLookup(const char* cvar, float& value)
    {
        const Site& site = FindSite("GraphicsLimits", "GameSliderMaximum");
        auto* code = static_cast<uint8_t*>(VirtualAlloc(nullptr, 0x100, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE));
        memset(code, 0xCC, 0x100);
        memcpy(code, site.find.data(), site.find.size());
        float* result = &s_sliderValue;
        Bytes found = Hex("D9 1D") + U32(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(result))) + Hex("B8 01 00 00 00 C3");
        Bytes missing = Hex("B8 02 00 00 00 C3");
        memcpy(code + 26, found.data(), found.size());
        memcpy(code + 0x34, missing.data(), missing.size());
        Edit edit;
        std::string problem = site.build(code, edit);
        memcpy(code + edit.offset, edit.bytes.data(), edit.bytes.size());
        s_sliderValue = -1;
        __asm
        {
            push ebx
            push esi
            mov ebx, 0B0B0B0B0h
            mov esi, cvar
            mov eax, code
            call eax
            mov dword ptr [s_path], eax
            mov dword ptr [s_ebxAfter], ebx
            pop esi
            pop ebx
        }
        VirtualFree(code, 0, MEM_RELEASE);
        value = s_sliderValue;
        Check(problem.empty() && s_ebxAfter == 0xB0B0B0B0, std::string("slider lookup for ") + cvar + " builds and keeps ebx");
        return s_path;
    }

    uint32_t s_args[3];

    extern "C" __declspec(naked) void FakeCreate()
    {
        __asm
        {
            mov eax, dword ptr [esp + 4]
            mov dword ptr [s_args], eax
            mov eax, dword ptr [esp + 8]
            mov dword ptr [s_args + 4], eax
            mov eax, dword ptr [esp + 0Ch]
            mov dword ptr [s_args + 8], eax
            mov eax, 55
            ret
        }
    }

    void CallSizeShim(void (*shim)())
    {
        __asm
        {
            push 40h
            push 40h
            push 40h
            push 1234h
            call shim
            add esp, 16
        }
    }

    int32_t s_frameDelta = 0;
    uint32_t s_savedEbp = 0;

    // Stands in for the rest of the client's mask lookup: reports how its frame was set up, then returns 77.
    extern "C" __declspec(naked) void FakeMaskLookupBody()
    {
        __asm
        {
            mov eax, esp
            sub eax, ebp
            mov dword ptr [s_frameDelta], eax
            mov eax, dword ptr [ebp]
            mov dword ptr [s_savedEbp], eax
            mov esp, ebp
            pop ebp
            mov eax, 77
            ret
        }
    }

    uint32_t s_maskResult = 0;
    uint32_t s_callerEbp = 0;

    uint32_t CallMaskLookup(uint32_t maskSize)
    {
        __asm
        {
            mov dword ptr [s_callerEbp], ebp
            mov eax, maskSize
            call PortraitMaskForSize
            mov dword ptr [s_maskResult], eax
        }
        return s_maskResult;
    }
}

void VisualTests()
{
    // Slider maximums, names matched without regard to case; others have none.
    Check(Maximum("farclip") == std::to_string(2477.0f), "farclip slider maximum 2477");
    Check(Maximum("environmentDetail") == std::to_string(2.5f), "environmentDetail slider maximum 2.5");
    Check(Maximum("GroundEffectDist") == std::to_string(250.0f), "GroundEffectDist (any case) slider maximum 250");
    Check(Maximum("groundEffectDensity") == std::to_string(256.0f), "groundEffectDensity slider maximum 256");
    Check(Maximum("gamma") == "none", "gamma has no maximum");

    // The generated lookup code takes the client's "found" path with the maximum loaded, or its "no maximum" path.
    float value = 0;
    Check(RunSliderLookup("farclip", value) == 1 && value == 2477.0f, "slider lookup: farclip loads 2477 on the found path");
    Check(RunSliderLookup("environmentDetail", value) == 1 && value == 2.5f, "slider lookup: environmentDetail loads 2.5");
    Check(RunSliderLookup("gamma", value) == 2, "slider lookup: gamma takes the no-maximum path");

    // The size shims set the render target's 2nd and 3rd and the texture's 1st and 2nd arguments to 256.
    g_createRenderTarget = reinterpret_cast<void*>(&FakeCreate);
    g_createTexture = reinterpret_cast<void*>(&FakeCreate);
    CallSizeShim(&RenderTargetAt256);
    Check(s_args[0] == 0x1234 && s_args[1] == 256 && s_args[2] == 256, "render target created at 256x256");
    CallSizeShim(&TextureAt256);
    Check(s_args[0] == 256 && s_args[1] == 256 && s_args[2] == 0x40, "texture created at 256x256");

    // The mask: opaque center, clear corners, a thin soft edge, then zeros the client may read.
    for (const Patch& patch : Patches())
    {
        if (strcmp(patch.id, "HdPortraits") == 0)
        {
            patch.configure(PatchConfig());
        }
    }
    const uint8_t* mask = g_portraitMask;
    int soft = 0;
    for (int i = 0; i < PortraitSize * PortraitSize; ++i)
    {
        soft += mask[i] > 0 && mask[i] < 255 ? 1 : 0;
    }
    bool padZero = true;
    for (int i = 0; i < PortraitMaskPadding; ++i)
    {
        padZero = padZero && mask[PortraitSize * PortraitSize + i] == 0;
    }
    Check(mask[128 * PortraitSize + 128] == 255 && mask[0] == 0 && soft > 500 && soft < 2000 && padZero,
        "mask: center 255, corner 0, " + std::to_string(soft) + " soft edge pixels, zero padding");
    Check(g_portraitMaskSlot.flags == 0 && g_portraitMaskSlot.size == 65536 && g_portraitMaskSlot.data == mask, "mask slot: 0, 65536 bytes, the mask");

    // Size 256 gets the slot; other sizes run the lookup's own prologue and go on into its body.
    g_maskLookupBody = reinterpret_cast<void*>(&FakeMaskLookupBody);
    Check(CallMaskLookup(256) == reinterpret_cast<uint32_t>(&g_portraitMaskSlot), "mask lookup: 256 returns the HD slot");
    Check(CallMaskLookup(64) == 77 && s_frameDelta == -0x504 && s_savedEbp == s_callerEbp,
        "mask lookup: 64 runs push ebp; mov ebp, esp; sub esp, 504h, then the client's body");
}
