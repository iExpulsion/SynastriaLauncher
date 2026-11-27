// Unit tests for Nameplate Unit Tokens: "nameplate<N>" parsing, the token names, the slot table's events (a new
// frame, a unit shown, a plate given back, a frame reused for another unit, changes inside one frame, a reload), and
// the assembly shims around AddNameplate, RemoveNameplate, the OnUpdate run and the token resolver.
#include <cstring>
#include <string>
#include <vector>
#include "../src/NameplateUnits.h"
#include "NativeTests.h"

namespace
{
    struct Fired
    {
        PlateEvent event;
        int slot;
        void* frame;
    };

    std::vector<Fired> s_fired;

    void __cdecl Record(void*, PlateEvent event, int slot, void* frame)
    {
        s_fired.push_back({ event, slot, frame });
    }

    std::vector<Fired> Flush(PlateSlots& slots)
    {
        s_fired.clear();
        slots.Flush(PlateEventSink { nullptr, &Record });
        return s_fired;
    }

    bool Is(const Fired& fired, PlateEvent event, int slot)
    {
        return fired.event == event && fired.slot == slot;
    }

    void* Frame(uintptr_t n)
    {
        return reinterpret_cast<void*>(0x1000 * n);
    }

    // A unit as the shims read it: the plate at +0xC38 and the descriptors (GUID first) at +0x8.
    alignas(4) uint8_t s_unit[0x1000];
    uint64_t s_descriptors[4];
    void* s_addUnit = nullptr;
    void* s_addPosition = nullptr;
    int s_addFlag = 0;
    void* s_removeSaw = nullptr;
    void* s_runThis = nullptr;
    float s_runDelta = 0;
    const char* s_resolvedToken = nullptr;

    void SetPlate(void* frame)
    {
        memcpy(s_unit + 0xC38, &frame, sizeof(frame));
    }

    // AddNameplate: thiscall, two arguments it pops; it gives the unit its plate.
    int __fastcall FakeAddNameplate(void* unit, void*, void* position, int flag)
    {
        s_addUnit = unit;
        s_addPosition = position;
        s_addFlag = flag;
        SetPlate(Frame(9));
        return 0x77;
    }

    // RemoveNameplate after the moved prologue (push esi; mov esi, ecx; mov eax, [esi+0xC38]): note eax, then return.
    __declspec(naked) void FakeRemoveRest()
    {
        __asm
        {
            mov s_removeSaw, eax
            pop esi
            ret
        }
    }

    // The frames' OnUpdate run: thiscall with the elapsed time, which it pops.
    void __fastcall FakeRunOnUpdates(void* self, void*, float delta)
    {
        s_runThis = self;
        s_runDelta = delta;
    }

    // The resolver after the moved prologue (push ebp; mov ebp, esp; push ecx; push edi): note the token, give GUID
    // 0x1234 and return true.
    __declspec(naked) void FakeResolveRest()
    {
        __asm
        {
            mov eax, [ebp + 8]
            mov s_resolvedToken, eax
            mov eax, [ebp + 12]
            mov dword ptr [eax], 1234h
            mov dword ptr [eax + 4], 0
            pop edi
            mov esp, ebp
            pop ebp
            mov al, 1
            ret
        }
    }

    void ShimTests()
    {
        s_descriptors[0] = 0xF130000000000042ull;
        uint64_t* descriptors = s_descriptors;
        memcpy(s_unit + 0x8, &descriptors, sizeof(descriptors));
        SetPlate(nullptr);
        g_addNameplate = reinterpret_cast<void*>(&FakeAddNameplate);
        void* unit = s_unit;
        void* shim = reinterpret_cast<void*>(&NameplateAddNameplate);
        uint32_t result = 0;
        uint32_t ok = 0;
        __asm
        {
            push ebx
            push esi
            push edi
            mov ebx, 0B0B0B0B0h
            mov esi, 5E5E5E5Eh
            mov edi, esp
            mov ecx, unit
            push 3
            push 0ABCDh
            call shim
            mov result, eax
            xor edx, edx
            cmp edi, esp
            jne addBad
            cmp ebx, 0B0B0B0B0h
            jne addBad
            cmp esi, 5E5E5E5Eh
            jne addBad
            mov edx, 1
        addBad:
            mov esp, edi
            pop edi
            pop esi
            pop ebx
            mov ok, edx
        }
        // The wrapper calls the client's AddNameplate with the unit and both arguments, keeps its result and the
        // caller's registers, pops what the client would, and gives the plate a slot for the unit.
        Check(ok == 1 && result == 0x77 && s_addUnit == s_unit && s_addPosition == reinterpret_cast<void*>(0xABCD) && s_addFlag == 3
            && g_plateSlots.FrameOf(0xF130000000000042ull) == Frame(9),
            "AddNameplate wrapper: passes the call through, then the shown plate gets a slot");

        g_removeNameplateResume = reinterpret_cast<void*>(&FakeRemoveRest);
        shim = reinterpret_cast<void*>(&NameplateRemoveNameplate);
        ok = 0;
        __asm
        {
            push ebx
            push esi
            push edi
            mov esi, 5E5E5E5Eh
            mov edi, esp
            mov ecx, unit
            call shim
            xor edx, edx
            cmp edi, esp
            jne removeBad
            cmp esi, 5E5E5E5Eh
            jne removeBad
            mov edx, 1
        removeBad:
            mov esp, edi
            pop edi
            pop esi
            pop ebx
            mov ok, edx
        }
        // The slot lets go of the unit first, then the client's function runs from its moved prologue (eax holds the
        // plate it loaded) with the caller's registers kept.
        Check(ok == 1 && s_removeSaw == Frame(9) && g_plateSlots.FrameOf(0xF130000000000042ull) == nullptr,
            "RemoveNameplate wrapper: the slot is cleared, then the client's code runs from its moved prologue");

        g_runOnUpdates = reinterpret_cast<void*>(&FakeRunOnUpdates);
        shim = reinterpret_cast<void*>(&NameplateFlushThenRunOnUpdates);
        float delta = 0.25f;
        ok = 0;
        __asm
        {
            push ebx
            push esi
            push edi
            mov edi, esp
            mov ecx, unit
            push delta
            call shim
            xor edx, edx
            cmp edi, esp
            jne runBad
            mov edx, 1
        runBad:
            mov esp, edi
            pop edi
            pop esi
            pop ebx
            mov ok, edx
        }
        // The flush shim hands on to the OnUpdate run with its object and argument untouched.
        Check(ok == 1 && s_runThis == s_unit && s_runDelta == 0.25f, "OnUpdate shim: hands on with the same object and elapsed time");

        g_resolveUnitTokenResume = reinterpret_cast<void*>(&FakeResolveRest);
        uint64_t guid = 0;
        bool resolved = NameplateResolveUnitToken("target", &guid, false);
        // Other tokens go to the client's resolver through the moved prologue.
        Check(resolved && guid == 0x1234 && std::string(s_resolvedToken) == "target", "resolver: other tokens go to the client's resolver");
        guid = 0;
        resolved = NameplateResolveUnitToken("nameplate60", &guid, false);
        // A plate token with no unit is valid and has no unit, like "target" with nothing targeted.
        Check(resolved && guid == 0xFFFFFFFFFFFFFFFEull, "resolver: a plate token with no unit gives the no-unit GUID");
        guid = 0;
        resolved = NameplateResolveUnitToken("nameplate60target", &guid, false);
        // A suffix on an empty plate resolves to nothing.
        Check(!resolved && guid == 0, "resolver: a suffix on an empty plate fails");
        g_addNameplate = nullptr;
        g_removeNameplateResume = nullptr;
        g_runOnUpdates = nullptr;
        g_resolveUnitTokenResume = nullptr;
    }
}

void NameplateUnitTests()
{
    int index = 0;
    const char* rest = nullptr;
    // A plain token gives its number and nothing after it.
    Check(ParsePlateToken("nameplate1", index, rest) && index == 1 && *rest == '\0', "plate token: nameplate1");
    // Any case is accepted, as the client accepts its own tokens.
    Check(ParsePlateToken("NamePlate12", index, rest) && index == 12, "plate token: any case");
    // A suffix is handed on for the client's resolver.
    Check(ParsePlateToken("nameplate3target", index, rest) && index == 3 && std::string(rest) == "target", "plate token: the suffix after the digits");
    // The last slot is in range; one past it, zero, a missing number and four digits aren't.
    Check(ParsePlateToken("nameplate64", index, rest) && index == 64, "plate token: nameplate64 is the last");
    Check(!ParsePlateToken("nameplate65", index, rest) && !ParsePlateToken("nameplate0", index, rest)
        && !ParsePlateToken("nameplate", index, rest) && !ParsePlateToken("nameplate1000", index, rest),
        "plate token: out-of-range and missing numbers are refused");
    // Other tokens are left to the client.
    Check(!ParsePlateToken("target", index, rest) && !ParsePlateToken(nullptr, index, rest) && !ParsePlateToken("name", index, rest),
        "plate token: other tokens are not plate tokens");
    // Token names match the slots.
    Check(std::string(PlateToken(1)) == "nameplate1" && std::string(PlateToken(64)) == "nameplate64" && std::string(PlateToken(65)).empty(),
        "plate token names");

    PlateSlots slots;
    // Nothing shown, nothing fired, no unit in a slot.
    Check(Flush(slots).empty() && slots.Unit(1) == 0 && !slots.Pending(), "plates: an empty table fires nothing");

    // A new frame showing a unit: created, then the unit added, in slot 1; the token resolves at once.
    slots.Shown(Frame(1), 101);
    Check(slots.Unit(1) == 101 && slots.FrameOf(101) == Frame(1) && slots.Pending(), "plates: a shown unit resolves before the flush");
    std::vector<Fired> first = Flush(slots);
    Check(first.size() == 2 && Is(first[0], PlateEvent::Created, 1) && Is(first[1], PlateEvent::UnitAdded, 1) && first[0].frame == Frame(1),
        "plates: a new frame fires Created, then UnitAdded");

    // The same unit shown again on later frames fires nothing more.
    slots.Shown(Frame(1), 101);
    Check(Flush(slots).empty() && !slots.Pending(), "plates: an unchanged plate fires nothing");

    // A second frame takes slot 2.
    slots.Shown(Frame(2), 202);
    Check(Flush(slots).size() == 2 && slots.Unit(2) == 202 && slots.Frame(2) == Frame(2), "plates: a second frame takes slot 2");

    // Given back: the token stops resolving at once and UnitRemoved fires.
    slots.Removed(Frame(1));
    Check(slots.Unit(1) == 0 && slots.FrameOf(101) == nullptr, "plates: a removed plate's token resolves to nothing right away");
    std::vector<Fired> removed = Flush(slots);
    Check(removed.size() == 1 && Is(removed[0], PlateEvent::UnitRemoved, 1), "plates: removing fires UnitRemoved");

    // The pooled frame comes back for another unit: same slot, no second Created.
    slots.Shown(Frame(1), 303);
    std::vector<Fired> reused = Flush(slots);
    Check(reused.size() == 1 && Is(reused[0], PlateEvent::UnitAdded, 1) && slots.Unit(1) == 303, "plates: a reused frame keeps its slot and only adds");

    // A frame switching units without a removal in between: removed, then added.
    slots.Shown(Frame(1), 404);
    std::vector<Fired> switched = Flush(slots);
    Check(switched.size() == 2 && Is(switched[0], PlateEvent::UnitRemoved, 1) && Is(switched[1], PlateEvent::UnitAdded, 1),
        "plates: a frame changing units fires UnitRemoved, then UnitAdded");

    // Removed and shown again within one frame: removed, then added.
    slots.Removed(Frame(2));
    slots.Shown(Frame(2), 505);
    std::vector<Fired> sameFrame = Flush(slots);
    Check(sameFrame.size() == 2 && Is(sameFrame[0], PlateEvent::UnitRemoved, 2) && Is(sameFrame[1], PlateEvent::UnitAdded, 2),
        "plates: removed and re-shown in one frame fires both, in order");

    // Shown and removed within one frame: never announced, so nothing fires but the new frame.
    slots.Shown(Frame(3), 606);
    slots.Removed(Frame(3));
    std::vector<Fired> brief = Flush(slots);
    Check(brief.size() == 1 && Is(brief[0], PlateEvent::Created, 3), "plates: a unit gone before the flush fires no UnitAdded or UnitRemoved");

    // Removing a plate twice fires one UnitRemoved.
    slots.Removed(Frame(1));
    slots.Removed(Frame(1));
    Check(Flush(slots).size() == 1, "plates: a second removal fires nothing more");

    // A reload announces every frame again and every unit still shown.
    slots.Reannounce();
    std::vector<Fired> reload = Flush(slots);
    int created = 0;
    int added = 0;
    for (const Fired& fired : reload)
    {
        created += fired.event == PlateEvent::Created ? 1 : 0;
        added += fired.event == PlateEvent::UnitAdded ? 1 : 0;
    }
    Check(created == 3 && added == 1 && slots.Unit(2) == 505, "plates: a reload creates every frame again and adds the units shown");

    // Past 64 frames, plates get no slot and nothing breaks.
    PlateSlots full;
    for (uintptr_t i = 1; i <= MaxPlateSlots + 1; ++i)
    {
        full.Shown(Frame(i), 1000 + i);
    }
    Check(full.Unit(64) == 1064 && full.FrameOf(1065) == nullptr && Flush(full).size() == 2 * MaxPlateSlots,
        "plates: the 65th frame gets no slot");

    // A unit of GUID 0 (no unit) is never added.
    PlateSlots empty;
    empty.Shown(Frame(1), 0);
    std::vector<Fired> none = Flush(empty);
    Check(none.size() == 1 && Is(none[0], PlateEvent::Created, 1) && empty.FrameOf(0) == nullptr, "plates: a frame with no unit is only created");

    ShimTests();
}
