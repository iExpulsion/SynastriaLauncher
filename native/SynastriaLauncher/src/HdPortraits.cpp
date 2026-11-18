#include <algorithm>
#include <cmath>
#include "HdPortraits.h"
#include "Patch.h"

void* g_createRenderTarget = nullptr;
void* g_createTexture = nullptr;
void* g_maskLookupBody = nullptr;

namespace
{
    uint8_t g_maskData[PortraitSize * PortraitSize + PortraitMaskPadding];
}

const uint8_t* g_portraitMask = g_maskData;
PortraitMaskSlot g_portraitMaskSlot { 0, PortraitSize * PortraitSize, g_maskData };

void BuildPortraitMask(uint8_t* mask)
{
    double radius = PortraitSize / 2.0 - 1.0;
    double center = (PortraitSize - 1) / 2.0;
    for (int y = 0; y < PortraitSize; ++y)
    {
        for (int x = 0; x < PortraitSize; ++x)
        {
            double distance = std::sqrt((x - center) * (x - center) + (y - center) * (y - center));
            double alpha = std::nearbyint((radius - distance + 0.5) * 255.0);
            mask[y * PortraitSize + x] = static_cast<uint8_t>(std::max(0.0, std::min(255.0, alpha)));
        }
    }
}

// The render target call's second and third arguments are its size.
extern "C" __declspec(naked) void RenderTargetAt256()
{
    __asm
    {
        mov dword ptr [esp + 8], 256
        mov dword ptr [esp + 0Ch], 256
        jmp dword ptr [g_createRenderTarget]
    }
}

// The texture call's first and second arguments are its size.
extern "C" __declspec(naked) void TextureAt256()
{
    __asm
    {
        mov dword ptr [esp + 4], 256
        mov dword ptr [esp + 8], 256
        jmp dword ptr [g_createTexture]
    }
}

// The lookup's own first instructions (push ebp; mov ebp, esp; sub esp, 504h) were replaced by the jump here, so
// other sizes run them before going on into the lookup's body.
extern "C" __declspec(naked) void PortraitMaskForSize()
{
    __asm
    {
        cmp eax, 256
        jne other
        mov eax, offset g_portraitMaskSlot
        ret
    other:
        push ebp
        mov ebp, esp
        sub esp, 504h
        jmp dword ptr [g_maskLookupBody]
    }
}

namespace
{
    constexpr uint32_t StockSize = 64;

    // The mask lookup's first 9 bytes, after one byte of padding before it.
    const Bytes MaskLookupPrologue = Hex("55 8B EC 81 EC 04 05 00 00");

    std::string JumpToMaskShim(uint8_t* match, Edit& edit)
    {
        uint8_t* function = match + 1;
        g_maskLookupBody = function + MaskLookupPrologue.size();
        return JumpTo(1, reinterpret_cast<const void*>(&PortraitMaskForSize), 4)(match, edit);
    }
}

Patch HdPortraitsPatch()
{
    return Patch
    {
        "HdPortraits",
        {
            Code("CreateRenderTarget", Hex("89 45 FC E8 93 0B EA FF 83 C4 2C A3"),
                CallTo(3, reinterpret_cast<const void*>(&RenderTargetAt256), &g_createRenderTarget)),
            Code("CreateTexture", Hex("40 6A 40 E8 89 F6 E9 FF 83 C4 24 89"),
                CallTo(3, reinterpret_cast<const void*>(&TextureAt256), &g_createTexture)),
            Swap("RenderLoopSize",
                Hex("83 C4 08") + Hex("B8") + U32(StockSize) + Hex("8D 55 F0 89"),
                Hex("83 C4 08") + Hex("B8") + U32(PortraitSize) + Hex("8D 55 F0 89")),
            Swap("TextureLoopSize",
                Hex("8D 55 E4") + Hex("BF") + U32(StockSize) + Hex("53 52 89 45"),
                Hex("8D 55 E4") + Hex("BF") + U32(PortraitSize) + Hex("53 52 89 45")),
            Code("MaskLookup", Hex("CC") + MaskLookupPrologue + Hex("53 56"), JumpToMaskShim),
        },
        [](const PatchConfig&)
        {
            g_createRenderTarget = nullptr;
            g_createTexture = nullptr;
            BuildPortraitMask(g_maskData);
        },
    };
}
