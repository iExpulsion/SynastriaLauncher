#include <cstring>
#include "Client.h"
#include "Patch.h"
#include "SmoothCursor.h"

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

    struct InputEvent
    {
        uint32_t type;
        uint32_t a;
        uint32_t b;
        uint32_t c;
        uint32_t time;
    };

    InputEvent g_pendingMove {};
    bool g_hasPendingMove = false;
    InputEvent g_heldEvent {};
    bool g_hasHeldEvent = false;
    bool g_movedThisPass = false;

    int Hand(const InputEvent& event, uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time)
    {
        *type = event.type;
        *a = event.a;
        *b = event.b;
        *c = event.c;
        *time = event.time;
        return 1;
    }

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
}

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

void* g_inputGet = nullptr;

void ForgetInputEvents()
{
    g_hasPendingMove = false;
    g_hasHeldEvent = false;
    g_movedThisPass = false;
}

extern "C" int __cdecl NextInputEvent(uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time)
{
    int result = 0;
    if (g_hasHeldEvent)
    {
        g_hasHeldEvent = false;
        result = Hand(g_heldEvent, type, a, b, c, time);
    }
    else
    {
        auto get = reinterpret_cast<InputGetFn>(g_inputGet);
        InputEvent event {};
        bool got = get(&event.type, &event.a, &event.b, &event.c, &event.time) != 0;
        while (got && event.type == Client::InputMouseMove)
        {
            g_pendingMove = event;
            g_hasPendingMove = true;
            got = get(&event.type, &event.a, &event.b, &event.c, &event.time) != 0;
        }
        if (got && g_hasPendingMove)
        {
            g_heldEvent = event;
            g_hasHeldEvent = true;
            g_hasPendingMove = false;
            g_movedThisPass = true;
            result = Hand(g_pendingMove, type, a, b, c, time);
        }
        else if (got)
        {
            result = Hand(event, type, a, b, c, time);
        }
        else if (g_hasPendingMove && !g_movedThisPass)
        {
            g_hasPendingMove = false;
            g_movedThisPass = true;
            result = Hand(g_pendingMove, type, a, b, c, time);
        }
    }
    return result;
}

extern "C" int __cdecl FirstInputEvent(uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time)
{
    g_movedThisPass = false;
    return NextInputEvent(type, a, b, c, time);
}

extern "C" int __cdecl CursorUploadDecision(uint8_t* device)
{
    return Field(device, Client::GxCursorUploads) != 0 && CursorUploadNeeded(device) ? 1 : 0;
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

// Moving the mouse costs frames two ways. The game handles every WM_MOUSEMOVE as its own input event, with the full
// mouse-over work each time, and since the mouse moves again while one is handled, a frame can handle several. And it
// uploads its cursor to the graphics device whenever anything marks the cursor changed, which DXVK makes slow (it
// rebuilds a Windows cursor each time, and with its enlarged cursor also hashes and logs it). This hands the game one
// mouse move per frame, the newest, and skips uploads that would send the image already shown.
Patch SmoothCursorPatch()
{
    constexpr size_t UnlockDirty = 7;
    constexpr size_t UpdateCheck = 47;
    constexpr size_t FirstInputCall = 6;
    constexpr size_t NextInputCall = 4;
    const Bytes updateTail = Hex("83 BB 54 29 00 00 00 0F 84 A2 00 00 00 83 BB 4C 3B 00 00 00 0F 84 95 00 00 00 "
        "8B 83 54 3B 00 00 85 C0 0F 84 87 00 00 00 83 BB 58 0F 00 00 00 74 7E");
    return Patch
    {
        "SmoothCursor",
        {
            Code("CursorUnlock", Hex("8B CE E8 5B 63 FF FF C7 86 4C 3B 00 00 01 00 00 00 5E 5D C2 08 00"),
                CallTo(UnlockDirty, reinterpret_cast<const void*>(&CursorChanged), nullptr, 5)),
            Code("CursorUpdate", Hex("8B CB E8 E2 4B 01 00") + updateTail,
                CallTo(UpdateCheck, reinterpret_cast<const void*>(&CursorUploadCheck), nullptr, 2)),
            Code("CursorUpdateEx", Hex("8B CB E8 42 75 00 00") + updateTail,
                CallTo(UpdateCheck, reinterpret_cast<const void*>(&CursorUploadCheck), nullptr, 2)),
            Code("InputPassFirst", Hex("8D 4D 08 51 33 FF E8 A6 99 3E 00 83 C4 14 85 C0 74 3F"),
                CallTo(FirstInputCall, reinterpret_cast<const void*>(&FirstInputEvent), &g_inputGet)),
            Code("InputPassNext", Hex("8D 45 08 50 E8 6A 99 3E 00 83 C4 14 85 C0 75 D3"),
                CallTo(NextInputCall, reinterpret_cast<const void*>(&NextInputEvent), &g_inputGet)),
        },
        [](const PatchConfig&) { ForgetCursorUpload(); ForgetInputEvents(); g_inputGet = nullptr; },
    };
}
