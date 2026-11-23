// Unit tests for Cursor Optimizations: when the cursor update skips an upload (only plain changes since the last upload, and
// the same image, hotspot, device and texture), when WM_SETCURSOR still hides the Windows cursor, and the three
// assembly shims called with the client's registers.
#include <cstring>
#include <vector>
#include "../src/Client.h"
#include "../src/CursorOptimizations.h"
#include "NativeTests.h"

namespace
{
    std::vector<uint8_t> g_device(0x4000);
    int g_hides = 0;

    uint32_t Get(uint32_t offset)
    {
        uint32_t value = 0;
        memcpy(&value, g_device.data() + offset, 4);
        return value;
    }

    void Set(uint32_t offset, uint32_t value)
    {
        memcpy(g_device.data() + offset, &value, 4);
    }

    void FreshDevice()
    {
        std::fill(g_device.begin(), g_device.end(), uint8_t(0));
        Set(Client::GxD3dDevice, 0x1000);
        Set(Client::GxCursorTexture, 0x2000);
        Set(Client::GxCursorUploads, 1);
        memset(g_device.data() + Client::GxCursorImage, 0x7F, Client::GxCursorImageSize);
        ForgetCursorUpload();
    }

    bool Upload(uint32_t dirty)
    {
        Set(Client::GxCursorDirty, dirty);
        return CursorUploadNeeded(g_device.data());
    }

    uint32_t RunChanged(uint32_t dirty)
    {
        Set(Client::GxCursorDirty, dirty);
        uint8_t* device = g_device.data();
        __asm
        {
            push esi
            mov esi, device
            call CursorChanged
            pop esi
        }
        return Get(Client::GxCursorDirty);
    }

    // Returns whether the shim set the zero flag (the client then skips), and the eax it left.
    bool RunCheck(uint32_t& eaxAfter)
    {
        uint8_t* device = g_device.data();
        uint8_t skip = 0;
        uint32_t eaxValue = 0;
        __asm
        {
            push ebx
            mov ebx, device
            mov eax, 0x12345678
            call CursorUploadCheck
            setz skip
            mov eaxValue, eax
            pop ebx
        }
        eaxAfter = eaxValue;
        return skip != 0;
    }

    void CountHide()
    {
        ++g_hides;
    }

    // Calls the WM_SETCURSOR shim as the window procedure does (esi = device, edi and ebx live); returns the hides and
    // whether esi, edi and ebx came back unchanged.
    int RunSetCursor(bool& kept)
    {
        uint8_t* device = g_device.data();
        uint32_t esiAfter = 0, ediAfter = 0, ebxAfter = 0;
        g_hides = 0;
        __asm
        {
            push esi
            push edi
            push ebx
            mov esi, device
            mov edi, 0x11111111
            mov ebx, 0x22222222
            call WindowSetCursor
            mov esiAfter, esi
            mov ediAfter, edi
            mov ebxAfter, ebx
            pop ebx
            pop edi
            pop esi
        }
        kept = esiAfter == reinterpret_cast<uint32_t>(device) && ediAfter == 0x11111111 && ebxAfter == 0x22222222;
        return g_hides;
    }
}

void CursorTests()
{
    // The first upload always goes through, and the same cursor after only a plain change is skipped and cleaned.
    FreshDevice();
    Check(Upload(CursorChangedOnly), "cursor: the first upload goes through");
    Check(!Upload(CursorChangedOnly) && Get(Client::GxCursorDirty) == 0, "cursor: the same cursor again is skipped and marked clean");

    // Anything that writes 1 (a device reset, the cursor shown again) uploads even an unchanged cursor.
    Check(Upload(1), "cursor: a pending 1 uploads the same cursor");

    // A new image, hotspot, device or texture uploads.
    g_device[Client::GxCursorImage + 100] ^= 0xFF;
    Check(Upload(CursorChangedOnly), "cursor: a changed pixel uploads");
    Set(Client::GxCursorHotspotX, 5);
    Check(Upload(CursorChangedOnly), "cursor: a changed hotspot uploads");
    Set(Client::GxCursorTexture, 0x3000);
    Check(Upload(CursorChangedOnly), "cursor: a new texture uploads");
    Set(Client::GxD3dDevice, 0x4000);
    Check(Upload(CursorChangedOnly), "cursor: a new device uploads");
    Check(!Upload(CursorChangedOnly), "cursor: then the same cursor is skipped again");

    // Switching the cursor away and back uploads both times, since each differs from the one shown.
    g_device[Client::GxCursorImage] ^= 0xFF;
    bool away = Upload(CursorChangedOnly);
    g_device[Client::GxCursorImage] ^= 0xFF;
    Check(away && Upload(CursorChangedOnly), "cursor: away and back both upload");

    // Forgetting (a new run of the patch) uploads again.
    ForgetCursorUpload();
    Check(Upload(CursorChangedOnly), "cursor: after forgetting, the cursor uploads");

    // The unlock shim marks a plain change with 2, keeps a pending 1, and keeps 2.
    Check(RunChanged(0) == CursorChangedOnly, "cursor unlock shim: clean becomes 2");
    Check(RunChanged(1) == 1, "cursor unlock shim: a pending 1 stays");
    Check(RunChanged(CursorChangedOnly) == CursorChangedOnly, "cursor unlock shim: 2 stays 2");

    // The update shim: zero flag set (skip) when uploads are off or the cursor is unchanged, clear to upload; eax kept.
    FreshDevice();
    uint32_t eaxAfter = 0;
    Set(Client::GxCursorDirty, CursorChangedOnly);
    Check(!RunCheck(eaxAfter) && eaxAfter == 0x12345678, "cursor update shim: the first cursor uploads, eax kept");
    Set(Client::GxCursorDirty, CursorChangedOnly);
    Check(RunCheck(eaxAfter) && eaxAfter == 0x12345678 && Get(Client::GxCursorDirty) == 0,
        "cursor update shim: the same cursor is skipped, eax kept");
    Set(Client::GxCursorUploads, 0);
    Set(Client::GxCursorDirty, 1);
    Check(RunCheck(eaxAfter) && Get(Client::GxCursorDirty) == 1, "cursor update shim: uploads off skips and leaves the flag");
    Set(Client::GxCursorUploads, 1);
    Check(!RunCheck(eaxAfter), "cursor update shim: once uploads are on, the pending 1 uploads");

    // WM_SETCURSOR keeps the Windows cursor while the hardware cursor is shown; any other case hides it as before.
    auto savedHide = g_hideWindowsCursor;
    g_hideWindowsCursor = &CountHide;
    bool kept = false;
    FreshDevice();
    Set(Client::GxCursorShown, 1);
    Set(Client::GxCursorHardware, 1);
    Check(HardwareCursorShown(g_device.data()) && RunSetCursor(kept) == 0 && kept,
        "set cursor shim: hardware cursor shown, nothing hidden, esi, edi and ebx kept");
    Set(Client::GxCursorHardware, 0);
    Check(!HardwareCursorShown(g_device.data()) && RunSetCursor(kept) == 1 && kept, "set cursor shim: software cursor hides it");
    Set(Client::GxCursorHardware, 1);
    Set(Client::GxCursorShown, 0);
    Check(RunSetCursor(kept) == 1 && kept, "set cursor shim: cursor not shown hides it");
    g_hideWindowsCursor = savedHide;
}
