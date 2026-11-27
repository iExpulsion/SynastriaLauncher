#include <cstdio>
#include <cstring>
#include <string>
#include "Client.h"
#include "NameplateUnits.h"
#include "Patch.h"

PlateSlots g_plateSlots;
void* g_resolveUnitTokenResume = nullptr;
void* g_addNameplate = nullptr;
void* g_removeNameplateResume = nullptr;
void* g_initializeEvents = nullptr;
void* g_runOnUpdates = nullptr;
bool g_plateEventsAdded = false;

PlateSlots::Slot* PlateSlots::Find(const void* frame)
{
    for (Slot& slot : m_slots)
    {
        if (slot.frame == frame)
        {
            return &slot;
        }
    }
    return nullptr;
}

void PlateSlots::Shown(void* frame, uint64_t guid)
{
    Slot* slot = Find(frame);
    Slot* fresh = slot == nullptr ? Find(nullptr) : nullptr;
    if (fresh != nullptr)
    {
        fresh->frame = frame;
        fresh->created = true;
        m_pending = true;
        slot = fresh;
    }
    if (slot != nullptr && slot->guid != guid)
    {
        slot->removed = slot->removed || slot->announced;
        slot->announced = false;
        slot->guid = guid;
        slot->added = guid != 0;
        m_pending = true;
    }
}

void PlateSlots::Removed(void* frame)
{
    Slot* slot = Find(frame);
    if (slot != nullptr && slot->guid != 0)
    {
        slot->removed = slot->removed || slot->announced;
        slot->announced = false;
        slot->added = false;
        slot->guid = 0;
        m_pending = m_pending || slot->removed;
    }
}

void PlateSlots::Clear()
{
    for (Slot& slot : m_slots)
    {
        slot = Slot();
    }
    m_pending = false;
}

void PlateSlots::Flush(const PlateEventSink& sink)
{
    m_pending = false;
    for (int i = 0; i < MaxPlateSlots; ++i)
    {
        Slot& slot = m_slots[i];
        if (slot.removed)
        {
            slot.removed = false;
            sink.fire(sink.context, PlateEvent::UnitRemoved, i + 1, slot.frame);
        }
        if (slot.created)
        {
            slot.created = false;
            sink.fire(sink.context, PlateEvent::Created, i + 1, slot.frame);
        }
        if (slot.added)
        {
            slot.added = false;
            slot.announced = true;
            sink.fire(sink.context, PlateEvent::UnitAdded, i + 1, slot.frame);
        }
    }
}

uint64_t PlateSlots::Unit(int index) const
{
    return index >= 1 && index <= MaxPlateSlots ? m_slots[index - 1].guid : 0;
}

void* PlateSlots::Frame(int index) const
{
    return index >= 1 && index <= MaxPlateSlots ? m_slots[index - 1].frame : nullptr;
}

void* PlateSlots::FrameOf(uint64_t guid) const
{
    for (const Slot& slot : m_slots)
    {
        if (guid != 0 && slot.guid == guid)
        {
            return slot.frame;
        }
    }
    return nullptr;
}

bool ParsePlateToken(const char* token, int& index, const char*& rest)
{
    constexpr size_t PrefixLength = 9;
    bool prefixed = token != nullptr && _strnicmp(token, "nameplate", PrefixLength) == 0;
    const char* p = prefixed ? token + PrefixLength : token;
    int value = 0;
    int digits = 0;
    while (prefixed && *p >= '0' && *p <= '9' && digits < 3)
    {
        value = value * 10 + (*p - '0');
        ++digits;
        ++p;
    }
    bool ok = prefixed && digits > 0 && value >= 1 && value <= MaxPlateSlots && !(*p >= '0' && *p <= '9');
    if (ok)
    {
        index = value;
        rest = p;
    }
    return ok;
}

const char* PlateToken(int index)
{
    static char tokens[MaxPlateSlots][16];
    static const bool built = []
    {
        for (int i = 0; i < MaxPlateSlots; ++i)
        {
            snprintf(tokens[i], sizeof(tokens[i]), "nameplate%d", i + 1);
        }
        return true;
    }();
    (void)built;
    return index >= 1 && index <= MaxPlateSlots ? tokens[index - 1] : "";
}

namespace
{
    constexpr int EventCreated = 0;
    constexpr int EventUnitAdded = 1;
    constexpr int EventUnitRemoved = 2;
    constexpr uint32_t AddedEventCount = 3;
    const char* const AddedEventNames[AddedEventCount] = { "NAME_PLATE_CREATED", "NAME_PLATE_UNIT_ADDED", "NAME_PLATE_UNIT_REMOVED" };

    // The client keeps pointers to the names, so the table lives as long as the game.
    const char* g_eventNames[Client::StockEventCount + AddedEventCount];
    int g_firstPlateEvent = 0;

    using SuffixFn = bool(__cdecl*)(const char* rest, uint64_t* guid, void* player);
    using ResolveFn = bool(__cdecl*)(const char* token, uint64_t* guid, bool emptyIsTarget);
    using GetActivePlayerFn = void*(__cdecl*)();
    using InitializeEventsFn = void(__cdecl*)(const char** names, uint32_t count);
    using SignalEventFn = void(__cdecl*)(int id, const char* format, ...);
    using DispatchEventFn = void(__cdecl*)(int id, void* L, int values);
    using CreateLuaObjectFn = void(__thiscall*)(void* object, int);
    using LuaFunction = int(__cdecl*)(void* L);
    using LuaCreateTableFn = void(__cdecl*)(void* L, int arrays, int records);
    using LuaPushStringFn = void(__cdecl*)(void* L, const char* text);
    using LuaPushCClosureFn = void(__cdecl*)(void* L, LuaFunction function, int upvalues);
    using LuaIndexFn = void(__cdecl*)(void* L, int index);
    using LuaRawGetIFn = void(__cdecl*)(void* L, int index, int n);
    using LuaPushNumberFn = void(__cdecl*)(void* L, double value);
    using LuaPushNilFn = void(__cdecl*)(void* L);
    using LuaTypeFn = int(__cdecl*)(void* L, int index);
    using LuaToLStringFn = const char*(__cdecl*)(void* L, int index, size_t* length);
    using LuaToUserdataFn = void*(__cdecl*)(void* L, int index);

    template <typename T>
    T& At(uintptr_t address)
    {
        return *reinterpret_cast<T*>(address);
    }

    template <typename T>
    T Field(const void* object, uint32_t offset)
    {
        T value;
        memcpy(&value, static_cast<const uint8_t*>(object) + offset, sizeof(T));
        return value;
    }

    void* Lua()
    {
        return At<void*>(Client::LuaState);
    }

    // Whether the frame's registry reference is its Lua object in this Lua state: a table holding the frame at key 0.
    bool HoldsFrame(void* L, void* frame)
    {
        auto rawGetI = reinterpret_cast<LuaRawGetIFn>(Client::LuaRawGetI);
        auto setTop = reinterpret_cast<LuaIndexFn>(Client::LuaSetTop);
        rawGetI(L, Client::LuaRegistryIndex, Field<int>(frame, Client::ScriptObjectLuaRef));
        bool table = reinterpret_cast<LuaTypeFn>(Client::LuaType)(L, -1) == Client::LuaTypeTable;
        if (table)
        {
            rawGetI(L, -1, 0);
        }
        bool holds = table && reinterpret_cast<LuaToUserdataFn>(Client::LuaToUserdata)(L, -1) == frame;
        setTop(L, table ? -3 : -2);
        return holds;
    }

    // The frame's Lua object, made as the client makes it the first time; a reference left over from before a UI
    // reload is dropped and the object made again.
    void PushFrame(void* L, void* frame)
    {
        uint32_t& count = *reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(frame) + Client::ScriptObjectHasLua);
        if (count != 0 && !HoldsFrame(L, frame))
        {
            count = 0;
        }
        if (count == 0)
        {
            reinterpret_cast<CreateLuaObjectFn>(Client::ScriptObjectCreateLua)(frame, 0);
        }
        reinterpret_cast<LuaRawGetIFn>(Client::LuaRawGetI)(L, Client::LuaRegistryIndex, Field<int>(frame, Client::ScriptObjectLuaRef));
    }

    // The client's live event table is the one with the plate events: the same size, and their names at their IDs.
    // The glue screens set up a table of their own, without them.
    bool PlateEventsLive()
    {
        auto* objects = At<uint8_t**>(Client::EventObjects);
        bool live = g_plateEventsAdded && objects != nullptr
            && At<uint32_t>(Client::EventCount) == Client::StockEventCount + AddedEventCount;
        for (uint32_t i = 0; i < AddedEventCount && live; ++i)
        {
            uint8_t* event = objects[g_firstPlateEvent + i];
            live = event != nullptr && Field<const char*>(event, Client::EventName) == AddedEventNames[i];
        }
        return live;
    }

    // What SignalEvent does, for an argument its format can't carry: the event's name and the frame go on the stack
    // and the frames listening for it run, untainted unless the caller keeps its taint, as the client's dispatch has it.
    void SignalWithFrame(int id, void* frame)
    {
        auto* objects = At<uint8_t**>(Client::EventObjects);
        uint8_t* event = objects != nullptr ? objects[id] : nullptr;
        uint32_t flags = event != nullptr ? Field<uint32_t>(event, Client::EventFlags) : 0;
        void* L = Lua();
        if (event != nullptr && (flags & 1) == 0 && flags != 0 && L != nullptr)
        {
            uint32_t taint = At<uint32_t>(Client::TaintSource);
            At<int>(Client::EventDepth) += 1;
            if (At<int>(Client::EventKeepTaint) == 0)
            {
                At<uint32_t>(Client::TaintSource) = 0;
            }
            reinterpret_cast<LuaPushStringFn>(Client::LuaPushString)(L, Field<const char*>(event, Client::EventName));
            PushFrame(L, frame);
            reinterpret_cast<DispatchEventFn>(Client::DispatchEvent)(id, L, 2);
            reinterpret_cast<LuaIndexFn>(Client::LuaSetTop)(L, -3);
            int depth = At<int>(Client::EventDepth);
            if (depth != 0 && At<int>(Client::EventKeepTaint) == 0)
            {
                At<uint32_t>(Client::TaintSource) = taint;
            }
            At<int>(Client::EventDepth) = depth - 1 > 0 ? depth - 1 : 0;
        }
    }

    void __cdecl FirePlateEvent(void*, PlateEvent event, int slot, void* frame)
    {
        auto signal = reinterpret_cast<SignalEventFn>(Client::SignalEvent);
        if (event == PlateEvent::Created)
        {
            SignalWithFrame(g_firstPlateEvent + EventCreated, frame);
        }
        else
        {
            int id = g_firstPlateEvent + (event == PlateEvent::UnitAdded ? EventUnitAdded : EventUnitRemoved);
            signal(id, "%s", PlateToken(slot));
        }
    }

    void __cdecl FlushPlateEvents()
    {
        if (g_plateEventsAdded && g_plateSlots.Pending() && Lua() != nullptr && PlateEventsLive())
        {
            g_plateSlots.Flush(PlateEventSink { nullptr, &FirePlateEvent });
        }
    }

    void __cdecl PlateShown(void* unit)
    {
        void* frame = Field<void*>(unit, Client::UnitNameplate);
        void* descriptors = Field<void*>(unit, Client::ObjectDescriptors);
        if (frame != nullptr && descriptors != nullptr)
        {
            g_plateSlots.Shown(frame, Field<uint64_t>(descriptors, 0));
        }
    }

    void __cdecl PlateRemoved(void* unit)
    {
        void* frame = Field<void*>(unit, Client::UnitNameplate);
        if (frame != nullptr)
        {
            g_plateSlots.Removed(frame);
        }
    }

    int __cdecl LuaGetNamePlates(void* L)
    {
        reinterpret_cast<LuaCreateTableFn>(Client::LuaCreateTable)(L, MaxPlateSlots, 0);
        int count = 0;
        for (int i = 1; i <= MaxPlateSlots; ++i)
        {
            void* frame = g_plateSlots.Frame(i);
            if (frame != nullptr && g_plateSlots.Unit(i) != 0)
            {
                reinterpret_cast<LuaPushNumberFn>(Client::LuaPushNumber)(L, ++count);
                PushFrame(L, frame);
                reinterpret_cast<LuaIndexFn>(Client::LuaRawSet)(L, -3);
            }
        }
        return 1;
    }

    int __cdecl LuaGetNamePlateForUnit(void* L)
    {
        bool isString = reinterpret_cast<LuaTypeFn>(Client::LuaType)(L, 1) == Client::LuaTypeString;
        const char* token = isString ? reinterpret_cast<LuaToLStringFn>(Client::LuaToLString)(L, 1, nullptr) : nullptr;
        uint64_t guid = 0;
        bool resolved = token != nullptr && reinterpret_cast<ResolveFn>(Client::ResolveUnitToken)(token, &guid, false);
        void* frame = resolved ? g_plateSlots.FrameOf(guid) : nullptr;
        if (frame != nullptr)
        {
            PushFrame(L, frame);
        }
        else
        {
            reinterpret_cast<LuaPushNilFn>(Client::LuaPushNil)(L);
        }
        return 1;
    }

    void AddFunction(void* L, const char* name, LuaFunction function)
    {
        reinterpret_cast<LuaPushStringFn>(Client::LuaPushString)(L, name);
        reinterpret_cast<LuaPushCClosureFn>(Client::LuaPushCClosure)(L, function, 0);
        reinterpret_cast<LuaIndexFn>(Client::LuaSetTable)(L, -3);
    }

    void CreateNamePlateTable(void* L)
    {
        reinterpret_cast<LuaCreateTableFn>(Client::LuaCreateTable)(L, 0, 2);
        AddFunction(L, "GetNamePlates", &LuaGetNamePlates);
        AddFunction(L, "GetNamePlateForUnit", &LuaGetNamePlateForUnit);
        reinterpret_cast<LuaPushStringFn>(Client::LuaPushString)(L, "C_NamePlate");
        reinterpret_cast<LuaIndexFn>(Client::LuaInsert)(L, -2);
        reinterpret_cast<LuaIndexFn>(Client::LuaSetTable)(L, Client::LuaGlobalsIndex);
    }

    std::string DetourResolver(uint8_t* match, Edit& edit)
    {
        g_resolveUnitTokenResume = match + Client::ResolveUnitTokenPrologue;
        return JumpTo(0, reinterpret_cast<const void*>(&NameplateResolveUnitToken))(match, edit);
    }

    std::string DetourRemove(uint8_t* match, Edit& edit)
    {
        g_removeNameplateResume = match + Client::RemoveNameplatePrologue;
        return JumpTo(0, reinterpret_cast<const void*>(&NameplateRemoveNameplate), Client::RemoveNameplatePrologue - 5)(match, edit);
    }
}

// The moved prologue (push ebp; mov ebp, esp; push ecx; push edi), then the rest of the client's resolver.
extern "C" __declspec(naked) bool NameplateOriginalResolveUnitToken()
{
    __asm
    {
        push ebp
        mov ebp, esp
        push ecx
        push edi
        jmp dword ptr [g_resolveUnitTokenResume]
    }
}

// "nameplate<N>" gives the slot's unit, then any suffix ("target", "pet") as the client resolves one; with no unit,
// a bare token is still valid (GUID -2, as the client gives "target" with nothing targeted). Other tokens go to the
// client's resolver.
extern "C" bool __cdecl NameplateResolveUnitToken(const char* token, uint64_t* guid, bool emptyIsTarget)
{
    int index = 0;
    const char* rest = nullptr;
    bool resolved = false;
    if (ParsePlateToken(token, index, rest))
    {
        *guid = g_plateSlots.Unit(index);
        if (*guid != 0)
        {
            void* player = reinterpret_cast<GetActivePlayerFn>(Client::GetActivePlayer)();
            resolved = reinterpret_cast<SuffixFn>(Client::ResolveTokenSuffix)(rest, guid, player);
        }
        else
        {
            resolved = *rest == '\0';
        }
        *guid = resolved && *guid == 0 ? Client::NoUnitGuid : resolved ? *guid : 0;
    }
    else
    {
        resolved = reinterpret_cast<ResolveFn>(&NameplateOriginalResolveUnitToken)(token, guid, emptyIsTarget);
    }
    return resolved;
}

// Stands in for the call to AddNameplate (thiscall on the unit, two arguments it pops): the client's call first,
// then the slot for the plate the unit now shows.
extern "C" __declspec(naked) void NameplateAddNameplate()
{
    __asm
    {
        push ecx
        push dword ptr [esp + 12]
        push dword ptr [esp + 12]
        call dword ptr [g_addNameplate]
        pop ecx
        pushad
        pushfd
        push ecx
        call PlateShown
        add esp, 4
        popfd
        popad
        ret 8
    }
}

// The moved prologue (push esi; mov esi, ecx; mov eax, [esi+0xC38]), then the rest of RemoveNameplate.
extern "C" __declspec(naked) void NameplateOriginalRemoveNameplate()
{
    __asm
    {
        push esi
        mov esi, ecx
        mov eax, dword ptr [esi + 0xC38]
        jmp dword ptr [g_removeNameplateResume]
    }
}

// RemoveNameplate's entry jumps here: the slot lets go of the unit before the client hides the plate.
extern "C" __declspec(naked) void NameplateRemoveNameplate()
{
    __asm
    {
        pushad
        pushfd
        push ecx
        call PlateRemoved
        add esp, 4
        popfd
        popad
        jmp NameplateOriginalRemoveNameplate
    }
}

// Stands in for FireOnUpdate's call that runs the frames' OnUpdate scripts: queued plate events go out first.
extern "C" __declspec(naked) void NameplateFlushThenRunOnUpdates()
{
    __asm
    {
        pushad
        pushfd
        call FlushPlateEvents
        popfd
        popad
        jmp dword ptr [g_runOnUpdates]
    }
}

// Stands in for the UI load's event table setup: the client's names with the three plate events after them, then
// C_NamePlate, which exists only when the events do. Runs again on every reload, so every plate is announced anew.
void __cdecl NameplateInitializeEvents(const char** names, uint32_t count)
{
    bool stock = names == reinterpret_cast<const char**>(Client::StockEventNames) && count == Client::StockEventCount;
    g_plateSlots.Clear();
    if (stock)
    {
        memcpy(g_eventNames, names, count * sizeof(const char*));
        memcpy(g_eventNames + count, AddedEventNames, sizeof(AddedEventNames));
        g_firstPlateEvent = static_cast<int>(count);
        reinterpret_cast<InitializeEventsFn>(g_initializeEvents)(g_eventNames, count + AddedEventCount);
        CreateNamePlateTable(Lua());
    }
    else
    {
        reinterpret_cast<InitializeEventsFn>(g_initializeEvents)(names, count);
    }
    g_plateEventsAdded = stock;
}

Patch NameplateUnitsPatch()
{
    return Patch
    {
        "NameplateUnits",
        {
            Code("ResolveUnitTokenEntry", Hex("55 8B EC 51 57 E8 96 8B EC FF 68 A0 00 00 00 68 40 23 9F 00 6A 10"), DetourResolver),
            Code("AddNameplateCall", Hex("57 8D 4D F4 51 8B CE E8 3E A3 FF FF"),
                CallTo(7, reinterpret_cast<const void*>(&NameplateAddNameplate), &g_addNameplate)),
            Code("RemoveNameplateEntry", Hex("56 8B F1 8B 86 38 0C 00 00 85 C0 74 3F 39 05 04 12 CA 00"), DetourRemove),
            Code("InitializeEvents", Hex("68 D2 02 00 00 68 B0 4E C2 00 E8 C6 0A 2F 00"),
                CallTo(10, reinterpret_cast<const void*>(&NameplateInitializeEvents), &g_initializeEvents)),
            Code("RunOnUpdates", Hex("51 8B CE D9 1C 24 E8 F8 FA FF FF 8B CE 5E 5D E9"),
                CallTo(6, reinterpret_cast<const void*>(&NameplateFlushThenRunOnUpdates), &g_runOnUpdates)),
        },
        [](const PatchConfig&)
        {
            g_resolveUnitTokenResume = nullptr;
            g_addNameplate = nullptr;
            g_removeNameplateResume = nullptr;
            g_initializeEvents = nullptr;
            g_runOnUpdates = nullptr;
            g_plateEventsAdded = false;
        },
    };
}
