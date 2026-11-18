#pragma once
#include <cstdint>

// Stock WoW 3.3.5a (build 12340) client addresses and struct offsets the patches use. The image has no relocations,
// so these are fixed. Functions a patch wraps at a call site are called through the address read from the matched
// instruction, not from here.
namespace Client
{
    constexpr uintptr_t FireOnUpdate = 0x495810;           // FrameScript_FireOnUpdate, registered as a per-frame callback

    // Object manager.
    constexpr uintptr_t GetActivePlayer = 0x4038F0;        // CGObject_C* ClntObjMgrGetActivePlayerObj()
    constexpr uintptr_t EnumVisibleObjects = 0x4D4B30;     // int ClntObjMgrEnumVisibleObjects(callback, context)
    constexpr uintptr_t ObjectPtr = 0x4D4DB0;              // CGObject_C* ClntObjMgrObjectPtr(guid, typeMask, file, line)
    constexpr uintptr_t ObjectPtrFile = 0x9E1F18;          // the source file name the client passes for its own calls
    constexpr uint32_t TypeMaskUnit = 0x8;

    // Game state.
    constexpr uintptr_t IsInWorld = 0xBD0792;              // byte, nonzero once the world is loaded
    constexpr uintptr_t TargetGuid = 0xBD07B0;             // uint64, the player's current target
    constexpr uintptr_t GetTimeMs = 0x86AE20;              // uint32 OsGetAsyncTimeMs()

    // CGObject_C virtual methods (byte offsets in the vtable).
    constexpr uint32_t VtableGetPosition = 0x2C;           // void GetPosition(C3Vector* out)

    // CGUnit_C fields.
    constexpr uint32_t UnitNameplate = 0xC38;              // CSimpleFrame*, the unit's nameplate when it has one

    // CSimpleFrame.
    constexpr uintptr_t SetFrameLevel = 0x4910A0;          // void CSimpleFrame::SetFrameLevel(int level, int), called with 1

    // Lua 5.1 C API.
    constexpr uintptr_t LuaGetTop = 0x84DBD0;              // int lua_gettop(lua_State*)
    constexpr uintptr_t LuaSetTop = 0x84DBF0;              // void lua_settop(lua_State*, int)
    constexpr uintptr_t LuaType = 0x84DEB0;                // int lua_type(lua_State*, int)
    constexpr uintptr_t LuaToLString = 0x84E0E0;           // const char* lua_tolstring(lua_State*, int, size_t*)
    constexpr int LuaTypeString = 4;

    // Spell targeting. While a cast waits for a target, the pending cast is non-null and the flags say what it
    // still needs, as the cast target flags: 0x40 a destination on the ground (the targeting circle, seen in game
    // for Death and Decay), 0x20 a source location.
    constexpr uintptr_t ScriptCastSpellByName = 0x540310;  // int Script_CastSpellByName(lua_State*)
    constexpr size_t ScriptCastSpellByNamePrologue = 9;    // push ebp; mov ebp, esp; sub esp, 0x2D0
    constexpr uintptr_t ScriptSpellStopTargeting = 0x809E30; // int Script_SpellStopTargeting(lua_State*), pushes 1 value
    constexpr uintptr_t PendingSpellCast = 0xD3F4E4;       // pointer, non-null while targeting
    constexpr uintptr_t PendingSpellNeeds = 0xD3F4E0;      // uint32 flags
    constexpr uint32_t NeedsGroundLocation = 0x20 | 0x40;
    constexpr uintptr_t HandleTerrainClick = 0x80C340;     // bool Spell_C_HandleTerrainClick(TerrainClick*), what a world click calls
    constexpr uint32_t LeftButton = 1;

    // The world frame and what's under the mouse. The world frame picks the object or ground under the cursor each
    // frame: a hover type of 1 or 3 means ground at the hover point, 2 an object (whose GUID is stored).
    constexpr uintptr_t WorldFrame = 0xB7436C;             // CGWorldFrame*
    // void __thiscall CGWorldFrame::UpdateHover(): picks what's under the mouse into the fields below. The client
    // only calls it from the world frame's mouse button handlers, so between clicks the fields hold the last click.
    constexpr uintptr_t WorldUpdateHover = 0x4FA570;
    constexpr uint32_t WorldHoverType = 0x2D8;
    constexpr uint32_t WorldHoverGuid = 0x2E0;             // uint64
    constexpr uint32_t WorldHoverPoint = 0x2E8;            // C3Vector
    constexpr uintptr_t SimpleTop = 0xB499A8;              // CSimpleTop*, the UI root
    constexpr uint32_t SimpleTopMouseFocus = 0x78;         // CSimpleFrame*, the frame under the mouse (GetMouseFocus)
    constexpr uint32_t TypeMaskObject = 0x1;

    // Idle tracking: the time of the last keyboard or mouse input.
    constexpr uintptr_t LastHardwareAction = 0xB499A4;     // uint32 ms

    // Constants the graphics settings code reads.
    constexpr uintptr_t HorizonScaleLimit = 0x9E8CF8;      // float, the horizon setting's stock upper limit
    constexpr uintptr_t FloatTwelve = 0xA1047C;            // float 12.0, used as the raised limit
    constexpr uintptr_t FarclipName = 0x9F57A0;            // "farclip"

    // Nameplate frames: a linked list the world frame lays out each pass.
    constexpr uintptr_t NameplateListHead = 0xADAA88;      // first plate; a pointer with bit 0 set ends the list
    constexpr uint32_t PlateNext = 0x2A0;
    constexpr uint32_t PlateScreenX = 0x2E0;               // float, raw layout position before SetPoint
    constexpr uint32_t PlateScreenY = 0x2E4;
    constexpr uint32_t PlateLayout = 0x20;                 // embedded layout object; its vtable gives the size
    constexpr uint32_t VtableGetWidth = 0x28;              // float GetWidth()
    constexpr uint32_t VtableGetHeight = 0x2C;             // float GetHeight()
}
