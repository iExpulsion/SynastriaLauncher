#pragma once
#include <cstdint>

// HD Portraits, from St0ny's AIO WoW EXE Patcher: unit frame portraits render the model into a 64x64 texture; the
// render target, the texture and the two loops that walk them are made 256x256, and the alpha mask lookup returns a
// 256x256 circular mask for that size.

constexpr int PortraitSize = 256;

// The mask the client asks for by size: a slot { flags, byte count, data }. The data is the mask followed by 4 KB of
// zeros, because the client reads past the end.
struct PortraitMaskSlot
{
    uint32_t flags;
    uint32_t size;
    const uint8_t* data;
};

extern PortraitMaskSlot g_portraitMaskSlot;
extern const uint8_t* g_portraitMask;
constexpr int PortraitMaskPadding = 0x1000;

// Fills the 256x256 mask: a circle filling the square, opaque inside with a one-pixel soft edge.
void BuildPortraitMask(uint8_t* mask);

extern void* g_createRenderTarget;
extern void* g_createTexture;
extern void* g_maskLookupBody;

// Called in place of the render target and texture creation: set the size arguments to 256, then go on.
extern "C" void RenderTargetAt256();
extern "C" void TextureAt256();

// Entered in place of the mask lookup (size in eax): returns the 256 mask's slot, or runs the client's lookup.
extern "C" void PortraitMaskForSize();
