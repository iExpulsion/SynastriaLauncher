#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include "Client.h"
#include "Patch.h"
#include "CursorOptimizations.h"

namespace
{
    struct CursorUpload
    {
        uint32_t d3dDevice;
        uint32_t texture;
        uint32_t hotspotX;
        uint32_t hotspotY;
        uint8_t image[Client::GxCursorImageSize];
    };

    CursorUpload g_last {};
    bool g_haveLast = false;

    uint32_t Field(const uint8_t* device, uint32_t offset)
    {
        uint32_t value = 0;
        memcpy(&value, device + offset, 4);
        return value;
    }

    CursorUpload Snapshot(const uint8_t* device)
    {
        CursorUpload upload {};
        upload.d3dDevice = Field(device, Client::GxD3dDevice);
        upload.texture = Field(device, Client::GxCursorTexture);
        upload.hotspotX = Field(device, Client::GxCursorHotspotX);
        upload.hotspotY = Field(device, Client::GxCursorHotspotY);
        memcpy(upload.image, device + Client::GxCursorImage, sizeof(upload.image));
        return upload;
    }

    void HideWindowsCursor()
    {
        SetCursor(nullptr);
    }
}

void (*g_hideWindowsCursor)() = &HideWindowsCursor;

void ForgetCursorUpload()
{
    g_haveLast = false;
}

bool CursorUploadNeeded(uint8_t* device)
{
    CursorUpload now = Snapshot(device);
    bool same = Field(device, Client::GxCursorDirty) == CursorChangedOnly && g_haveLast
        && memcmp(&now, &g_last, sizeof(now)) == 0;
    if (same)
    {
        uint32_t clean = 0;
        memcpy(device + Client::GxCursorDirty, &clean, 4);
    }
    else
    {
        g_last = now;
        g_haveLast = true;
    }
    return !same;
}

bool HardwareCursorShown(const uint8_t* device)
{
    return Field(device, Client::GxCursorShown) != 0 && Field(device, Client::GxCursorHardware) != 0;
}

extern "C" int __cdecl CursorUploadDecision(uint8_t* device)
{
    return Field(device, Client::GxCursorUploads) != 0 && CursorUploadNeeded(device) ? 1 : 0;
}

extern "C" void __cdecl SetWindowsCursorFor(uint8_t* device)
{
    if (!HardwareCursorShown(device))
    {
        g_hideWindowsCursor();
    }
}

// Replaces the cursor unlock's "dirty = 1" (esi = the device); keeps a pending 1.
extern "C" __declspec(naked) void CursorChanged()
{
    __asm
    {
        cmp dword ptr [esi + 0x3B4C], 1
        je done
        mov dword ptr [esi + 0x3B4C], 2
    done:
        ret
    }
}

// Replaces the update's "cmp [ebx+0xF58], 0" (ebx = the device) before its "je skip": zero flag set means skip.
extern "C" __declspec(naked) void CursorUploadCheck()
{
    __asm
    {
        push eax
        push ebx
        call CursorUploadDecision
        add esp, 4
        test eax, eax
        pop eax
        ret
    }
}

// Replaces WM_SETCURSOR's "SetCursor(NULL)" (esi = the device); like that call, it may change eax, ecx and edx.
extern "C" __declspec(naked) void WindowSetCursor()
{
    __asm
    {
        push esi
        call SetWindowsCursorFor
        add esp, 4
        ret
    }
}

// Moving the mouse costs frames two ways. On every WM_SETCURSOR (one per mouse move) the game hides the Windows cursor
// and then has the graphics device show its hardware cursor again, so Windows switches the cursor to nothing and back
// each time, which is slow. And it uploads its cursor to the graphics device whenever anything marks the cursor
// changed, which DXVK makes slow (it rebuilds a Windows cursor each time, and with its enlarged cursor also hashes and
// logs it). This leaves the hardware cursor in place while it's shown, and skips uploads that would send the image
// already shown.
Patch CursorOptimizationsPatch()
{
    constexpr size_t UnlockDirty = 7;
    constexpr size_t UpdateCheck = 47;
    constexpr size_t HideCall = 15;
    const Bytes updateTail = Hex("83 BB 54 29 00 00 00 0F 84 A2 00 00 00 83 BB 4C 3B 00 00 00 0F 84 95 00 00 00 "
        "8B 83 54 3B 00 00 85 C0 0F 84 87 00 00 00 83 BB 58 0F 00 00 00 74 7E");
    return Patch
    {
        "CursorOptimizations",
        {
            Code("CursorUnlock", Hex("8B CE E8 5B 63 FF FF C7 86 4C 3B 00 00 01 00 00 00 5E 5D C2 08 00"),
                CallTo(UnlockDirty, reinterpret_cast<const void*>(&CursorChanged), nullptr, 5)),
            Code("CursorUpdate", Hex("8B CB E8 E2 4B 01 00") + updateTail,
                CallTo(UpdateCheck, reinterpret_cast<const void*>(&CursorUploadCheck), nullptr, 2)),
            Code("CursorUpdateEx", Hex("8B CB E8 42 75 00 00") + updateTail,
                CallTo(UpdateCheck, reinterpret_cast<const void*>(&CursorUploadCheck), nullptr, 2)),
            Code("WindowSetCursor", Hex("83 BE 7C 39 00 00 00 74 69 66 83 FF 01 75 63 6A 00 FF 15 D4 F5 9D 00 83 BE 50 29 00 00 00"),
                CallTo(HideCall, reinterpret_cast<const void*>(&WindowSetCursor), nullptr, 3)),
        },
        [](const PatchConfig&) { ForgetCursorUpload(); },
    };
}
