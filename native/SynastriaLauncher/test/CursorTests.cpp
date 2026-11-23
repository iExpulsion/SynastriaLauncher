// Unit tests for Smooth Cursor: when the cursor update skips an upload (only plain changes since the last upload, and
// the same image, hotspot, device and texture), the two assembly shims called with the client's registers, and the
// input pass handing on one mouse move per frame without reordering other events.
#include <cstring>
#include <deque>
#include <string>
#include <vector>
#include "../src/Client.h"
#include "../src/SmoothCursor.h"
#include "NativeTests.h"

namespace
{
    std::vector<uint8_t> g_device(0x4000);

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

    constexpr uint32_t Move = Client::InputMouseMove;
    constexpr uint32_t Click = 7;
    constexpr uint32_t Look = 12;

    struct Event
    {
        uint32_t type;
        uint32_t x;
    };

    std::deque<Event> g_waiting;

    int __cdecl MockInputGet(uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time)
    {
        int got = 0;
        if (!g_waiting.empty())
        {
            *type = g_waiting.front().type;
            *a = 0;
            *b = g_waiting.front().x;
            *c = 0;
            *time = 0;
            g_waiting.pop_front();
            got = 1;
        }
        return got;
    }

    // One frame of the game's input loop, as "type:x" entries for what it receives.
    std::string Frame(std::deque<Event> arriving)
    {
        g_waiting = arriving;
        std::string seen;
        uint32_t type = 0, a = 0, b = 0, c = 0, time = 0;
        int more = FirstInputEvent(&type, &a, &b, &c, &time);
        while (more != 0)
        {
            seen += (seen.empty() ? "" : " ") + std::to_string(type) + ":" + std::to_string(b);
            more = NextInputEvent(&type, &a, &b, &c, &time);
        }
        return seen;
    }

    void InputTests()
    {
        void* saved = g_inputGet;
        g_inputGet = reinterpret_cast<void*>(&MockInputGet);
        ForgetInputEvents();
        std::string m = std::to_string(Move) + ":";

        // Mouse moves in a row reach the game as one, the newest.
        Check(Frame({ { Move, 1 }, { Move, 2 }, { Move, 3 } }) == m + "3", "input: three moves become the newest one");

        // A frame with nothing arriving hands on nothing; a single move passes as it is.
        Check(Frame({}).empty() && Frame({ { Move, 4 } }) == m + "4", "input: no events, and a single move");

        // A click gets the move before it first, so it lands where the mouse was; a move after it waits a frame.
        Check(Frame({ { Move, 5 }, { Move, 6 }, { Click, 6 }, { Move, 7 } }) == m + "6 7:6",
            "input: the move before a click comes first, the click next, the later move is held");
        Check(Frame({}) == m + "7", "input: the held move arrives in the next frame");

        // Two moves after a frame's move: the newer is held for the next frame, then replaced by a newer one.
        Check(Frame({ { Move, 8 }, { Click, 8 }, { Move, 9 } }) == m + "8 7:8", "input: a move after the frame's move waits");
        Check(Frame({ { Move, 10 } }) == m + "10", "input: a newer move replaces the held one");

        // Other events, mouselook's deltas included, all pass in order and are never merged.
        Check(Frame({ { Look, 1 }, { Look, 2 }, { Click, 3 } }) == "12:1 12:2 7:3", "input: mouselook deltas and clicks pass in order");

        g_inputGet = saved;
        ForgetInputEvents();
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

    InputTests();
}
