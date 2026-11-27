#pragma once
#include <cstdint>

// Stock WoW 3.3.5a (build 12340) client addresses and struct offsets the patches use. The image has no relocations,
// so these are fixed. Functions a patch wraps at a call site are called through the address read from the matched
// instruction, not from here.
namespace Client
{
    constexpr uintptr_t FireOnUpdate = 0x495810;           // FrameScript_FireOnUpdate, registered as a per-frame callback
    constexpr size_t FireOnUpdatePrologue = 6;             // push ebp; mov ebp, esp; fld dword ptr [ebp+0x14]
    constexpr uintptr_t FrameScriptExecute = 0x819210;     // void FrameScript_Execute(const char* code, const char* chunk, int), as RunScript calls it

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

    // Interacting. InteractUnit (a protected Lua function) resolves its unit, reports a blocked action when tainted
    // code called it, and otherwise calls OnInteract, as a right-click on the object does.
    constexpr size_t ScriptInteractUnitPrologue = 7;       // push ebp; mov ebp, esp; push edi; mov edi, [ebp+8]
    constexpr uintptr_t OnInteract = 0x5277B0;             // int OnInteract(uint64 guid): targets the object, then interacts
    constexpr uintptr_t TaintSource = 0xD4139C;            // nonzero while tainted Lua code runs
    constexpr uintptr_t ReportBlockedAction = 0x513530;    // void (int, int), called with 0, 0
    constexpr uintptr_t GameTimeMs = 0xCD76AC;             // uint32, what the loot check compares against
    constexpr uintptr_t LuaPushNil = 0x84E280;             // void lua_pushnil(lua_State*)
    constexpr uintptr_t LuaPushNumber = 0x84E2A0;          // void lua_pushnumber(lua_State*, double)

    // Key bindings. The game UI loads Interface\FrameXML\Bindings.xml right after FrameXML.toc, before addons and the
    // saved keys: the file is parsed into an XML tree and each <Binding> node registered (its script compiled).
    constexpr uintptr_t LoadBindingNode = 0x564470;        // int __thiscall CGBindings::LoadBinding(file, XMLNode*, CStatus*)
    constexpr uintptr_t XmlParse = 0x814D90;               // XMLTree* (const char* text, uint32 length); null on bad XML
    constexpr uintptr_t XmlFree = 0x814D60;                // void (XMLTree*)
    constexpr uint32_t XmlFirstChild = 0x8;                // XMLNode* of the tree's root node (*tree)
    constexpr uint32_t XmlNextSibling = 0x34;

    // What the hover cursor logic asks before showing an interaction cursor.
    constexpr uintptr_t UnitCanLoot = 0x717B60;            // int __thiscall CGUnit_C::CanLoot(uint32 now)
    constexpr uintptr_t PlayerCanInteract = 0x729620;      // bool __thiscall (player, unit): a friendly NPC the player can use
    constexpr uintptr_t UnitGatherLock = 0x53BCE0;         // uint32 (unit): the lock skinning it needs, 0 for none
    constexpr uintptr_t GameObjectCursor = 0x70CE10;       // int __thiscall CGGameObject_C::GetCursor()
    constexpr uintptr_t UnitGatherType = 0x715E50;         // int __thiscall (unit): what gathering a corpse takes, 0-3
    constexpr uint32_t GatherSkills[] = { 393, 182, 186, 202 }; // by gather type: Skinning, Herbalism, Mining, Engineering
    constexpr uintptr_t PlayerSkillIndex = 0x6DC1C0;       // int __thiscall (player, skill line): its slot, or -1
    constexpr uintptr_t PlayerSkillValue = 0x51A250;       // int __thiscall (player, slot): the skill with bonuses
    constexpr int CursorNone = 0;
    constexpr int CursorPickup = 8;
    constexpr int CursorLootAll = 16;
    constexpr int CursorFirstUnable = 27;                  // "Unable" cursors follow the usable ones
    constexpr int CursorCustom = 0x35;                     // a cursor named by the object's template
    constexpr uint32_t GameObjectBehavior = 0x1A0;         // the object type's handler
    constexpr uint32_t VtableUsableNow = 0x1C;             // bool __thiscall (handler, 0, 0, 0)

    constexpr uint32_t ObjectTypeId = 0x14;                // 3 unit, 4 player, 5 game object
    constexpr uint32_t TypeUnit = 3;
    constexpr uint32_t TypeGameObject = 5;
    constexpr uint32_t VtableGetFacing = 0x34;             // float GetFacing()
    constexpr uint32_t VtableCanHover = 0xA4;              // int CanHover(): zero for objects that show no cursor
    constexpr uint32_t UnitFields = 0xD0;                  // the unit's update fields (descriptors + 0x18)
    constexpr uint32_t UnitFieldHealth = 0x48;
    constexpr uint32_t UnitFieldFlags = 0xD4;
    constexpr uint32_t UnitFlagSkinnable = 0x04000000;
    constexpr uint32_t UnitFieldCombatReach = 0xF0;        // float
    constexpr uint32_t UnitFieldNpcFlags = 0x130;
    constexpr uint32_t UnitFieldLevel = 0xC0;

    // The camera. The world frame holds the active camera, whose projection reads its angle every frame.
    constexpr uint32_t WorldActiveCamera = 0x7E20;         // CGCamera*
    constexpr uint32_t CameraFov = 0x40;                   // float radians
    constexpr float RadiansPerDegree = 3.14159265f / 180.0f;

    // Console variables. Register returns the existing variable when one was already read from Config.wtf, running
    // its change handler with the saved value. Set runs the handler first and stores the value only when it accepts.
    constexpr uintptr_t RegisterCvar = 0x767FC0;           // CVar* (name, help, flags, value, handler, category, bool, user, bool)
    constexpr uintptr_t SetCvar = 0x7668C0;                // bool __thiscall CVar::Set(value, runHandler, bool, bool, bool)
    constexpr uint32_t CvarFlagsCamera = 0x10;             // what the stock camera settings pass
    constexpr uint32_t CvarCategoryCamera = 5;
    constexpr uintptr_t ConsoleWrite = 0x765360;           // void (format, color, ...), the console's printf
    constexpr uintptr_t OutOfRangeFormat = 0xA1E384;       // "Value out of range (%f - %f)"

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

    // The graphics device's hardware cursor (both D3D9 device classes). Changing the cursor marks it dirty, and the
    // device's cursor update then copies the image into its cursor texture and calls SetCursorProperties.
    constexpr uint32_t GxCursorHotspotX = 0x2958;
    constexpr uint32_t GxCursorHotspotY = 0x295C;
    constexpr uint32_t GxCursorImage = 0x2960;             // 32 rows of 32 A8R8G8B8 pixels
    constexpr uint32_t GxCursorImageSize = 32 * 32 * 4;
    constexpr uint32_t GxD3dDevice = 0x397C;               // IDirect3DDevice9*
    constexpr uint32_t GxCursorDirty = 0x3B4C;             // nonzero: upload at the next cursor update
    constexpr uint32_t GxCursorTexture = 0x3B54;           // IDirect3DTexture9*, managed, so it outlives a device reset
    constexpr uint32_t GxCursorUploads = 0xF58;            // the update uploads only while this is nonzero
    // On WM_SETCURSOR over the game the window procedure calls SetCursor(NULL), then the device's ShowCursor with
    // whether both of these are nonzero: the cursor is shown, and the hardware cursor is in use.
    constexpr uint32_t GxCursorShown = 0x2950;
    constexpr uint32_t GxCursorHardware = 0x2954;

    // Unit tokens. Every unit function, tooltip SetUnit and InteractUnit turn a token into a GUID through one resolver,
    // bool (const char* token, uint64* guid, bool emptyIsTarget). It reads the base token, then hands the rest
    // ("target", "pet" chains) to the suffix resolver, bool (const char* rest, uint64* guid, CGObject* player). A
    // valid token with no unit gives true and the GUID -2.
    constexpr uintptr_t ResolveUnitToken = 0x60ABF0;
    constexpr size_t ResolveUnitTokenPrologue = 5;         // push ebp; mov ebp, esp; push ecx; push edi
    constexpr uintptr_t ResolveTokenSuffix = 0x60AAA0;
    constexpr uint64_t NoUnitGuid = 0xFFFFFFFFFFFFFFFEull;
    constexpr uint32_t ObjectDescriptors = 0x8;            // the object's fields; its GUID comes first

    // A unit's nameplate. AddNameplate runs each frame for every unit that shows one (taking a frame from the pool the
    // first time); RemoveNameplate gives it back and hides it. Both are thiscall on the unit.
    constexpr uintptr_t AddNameplate = 0x7256C0;           // (C3Vector* position, int), ret 8
    constexpr uintptr_t RemoveNameplate = 0x725840;
    constexpr size_t RemoveNameplatePrologue = 9;          // push esi; mov esi, ecx; mov eax, [esi+0xC38]

    // FrameScript: Lua functions, frames as Lua objects, and events.
    constexpr uintptr_t LuaState = 0xD3F78C;               // lua_State*
    constexpr uintptr_t LuaCreateTable = 0x84E6E0;         // void lua_createtable(L, narr, nrec)
    constexpr uintptr_t LuaPushString = 0x84E350;          // void lua_pushstring(L, const char*)
    constexpr uintptr_t LuaPushCClosure = 0x84E400;        // void lua_pushcclosure(L, lua_CFunction, n)
    constexpr uintptr_t LuaInsert = 0x84DCC0;              // void lua_insert(L, index)
    constexpr uintptr_t LuaSetTable = 0x84E970;            // void lua_settable(L, index)
    constexpr uintptr_t LuaRawSet = 0x84E8D0;              // void lua_rawset(L, index)
    constexpr uintptr_t LuaRawGetI = 0x84E670;             // void lua_rawgeti(L, index, n)
    constexpr int LuaRegistryIndex = -10000;
    constexpr int LuaGlobalsIndex = -10002;
    constexpr uint32_t ScriptObjectHasLua = 0x4;           // nonzero once the frame's Lua object exists
    constexpr uint32_t ScriptObjectLuaRef = 0x8;           // its reference in the Lua registry
    constexpr uintptr_t ScriptObjectCreateLua = 0x819880;  // void __thiscall CScriptObject::CreateLuaObject(int), called with 0
    constexpr uintptr_t InitializeEvents = 0x81B5F0;       // void (const char** names, uint32 count); IDs are the indexes
    constexpr uintptr_t StockEventNames = 0xC24EB0;
    constexpr uint32_t StockEventCount = 722;
    constexpr uintptr_t SignalEvent = 0x81B530;            // void (int id, const char* format, ...): %b %d %f %s %u only
    constexpr uintptr_t DispatchEvent = 0x81AA00;          // void (int id, lua_State*, int values): the name and its arguments on the stack
    constexpr uintptr_t EventObjects = 0xD3F7D8;           // event object*[count]
    constexpr uint32_t EventName = 0x14;
    constexpr uint32_t EventFlags = 0x20;                  // bit 0 set: suppressed; zero: no frame listens
    constexpr uintptr_t EventDepth = 0xD413A0;             // events being dispatched
    constexpr uintptr_t EventKeepTaint = 0xD413A4;         // nonzero: dispatch keeps the current taint
}
