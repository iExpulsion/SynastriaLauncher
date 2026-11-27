#pragma once
#include <cstdint>

// Nameplate unit tokens, the nameplate API other 3.3.5a clients get from AwesomeWotLK, written from the stock client.
// Every nameplate frame gets a slot, so "nameplate1" to "nameplate64" name the unit a plate shows, wherever a unit
// token goes (UnitName, UnitAura, GameTooltip:SetUnit and the rest): the client's one token resolver has a detour
// for them. C_NamePlate.GetNamePlates() lists the plates showing a unit, and C_NamePlate.GetNamePlateForUnit(unit)
// gives the plate of any unit that has one. NAME_PLATE_CREATED (the frame), NAME_PLATE_UNIT_ADDED and
// NAME_PLATE_UNIT_REMOVED (the token) are added to the client's event names when the UI loads, and C_NamePlate is
// made then too, only when the client's table is the stock one the events can be appended to.
//
// The slots follow the client's own calls: a hook after AddNameplate (each frame, for every unit showing a plate) and
// one before RemoveNameplate, so a token stops resolving as soon as its plate is given back. The events wait for the
// next frame's FireOnUpdate rather than run inside the client's unit update. A plate keeps its slot for as long as the
// frame lives (the client reuses plate frames from a pool), so a plate's token doesn't change.

constexpr int MaxPlateSlots = 64;

enum class PlateEvent
{
    Created,
    UnitAdded,
    UnitRemoved,
};

// Receives the events a flush fires, in order.
struct PlateEventSink
{
    void* context;
    void (*fire)(void* context, PlateEvent event, int slot, void* frame);
};

class PlateSlots
{
public:
    // A unit showing this plate, after the client's AddNameplate. The first sighting of a frame takes the lowest free
    // slot and queues Created; a new unit queues UnitAdded, after UnitRemoved for one announced before.
    void Shown(void* frame, uint64_t guid);

    // The plate is being given back. UnitRemoved is queued only for a unit that was announced.
    void Removed(void* frame);

    // A UI load: forgets every frame and unit, with nothing queued. The client drops all plates when the UI unloads
    // without calling RemoveNameplate and may free its pooled frames when leaving the world, so nothing from before is
    // kept; the plates shown on the next frame come back as new.
    void Clear();

    // Fires what's queued: per slot, UnitRemoved, then Created, then UnitAdded.
    void Flush(const PlateEventSink& sink);

    // The unit in slot `index` (1-based, as in "nameplate1"); 0 when none.
    uint64_t Unit(int index) const;

    // The frame showing this unit; null when none.
    void* FrameOf(uint64_t guid) const;

    // The frame in slot `index` (1-based); null when the slot is free.
    void* Frame(int index) const;

    bool Pending() const { return m_pending; }

private:
    struct Slot
    {
        void* frame = nullptr;
        uint64_t guid = 0;
        bool announced = false;
        bool created = false;
        bool added = false;
        bool removed = false;
    };

    Slot* Find(const void* frame);

    Slot m_slots[MaxPlateSlots];
    bool m_pending = false;
};

// For "nameplate<N><rest>", any case: N (1 to MaxPlateSlots) and what follows the digits. False for any other token.
bool ParsePlateToken(const char* token, int& index, const char*& rest);

// The token for a slot: "nameplate" and its 1-based number.
const char* PlateToken(int index);

extern PlateSlots g_plateSlots;

extern void* g_resolveUnitTokenResume;   // the client's resolver, after the moved prologue
extern void* g_addNameplate;             // the client's AddNameplate, called through by the wrapper
extern void* g_removeNameplateResume;    // RemoveNameplate, after the moved prologue
extern void* g_initializeEvents;         // the client's event table setup
extern void* g_runOnUpdates;             // what FireOnUpdate calls first: the frames' OnUpdate scripts
extern bool g_plateEventsAdded;          // the three event names made it into the client's table

extern "C" bool __cdecl NameplateResolveUnitToken(const char* token, uint64_t* guid, bool emptyIsTarget);
extern "C" bool NameplateOriginalResolveUnitToken();
extern "C" void NameplateAddNameplate();
extern "C" void NameplateRemoveNameplate();
extern "C" void NameplateFlushThenRunOnUpdates();
void __cdecl NameplateInitializeEvents(const char** names, uint32_t count);
