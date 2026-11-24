#pragma once
#include <cstddef>
#include <cstdint>

// Interact Key: InteractUnit("nearest") interacts with the nearest thing in front of the player, and the stock
// Interact With Mouseover binding (InteractUnit("mouseover"), then "target") does the same when the mouse isn't over
// a unit. InteractUnit's entry jumps to a wrapper (a detour, like Ground Target Macros) that keeps the function's
// protection: from tainted code it reports a blocked action, as the client does. What counts as interactable follows
// the client's hover cursor logic: a lootable corpse, a friendly NPC with something to offer, a skinnable corpse, or
// a game object whose cursor isn't an "unable" one. The interaction goes through the client's right-click path.

struct InteractCandidate
{
    uint64_t guid;
    float position[3];
    float reach;        // the unit's combat reach, added to the range
};

struct InteractViewer
{
    float position[3];
    float facing;
};

constexpr float InteractRange = 6.0f;          // yards from the object's position, plus its reach
constexpr float InteractAnyDirection = 2.5f;   // closer than this counts whatever way the player faces

// The nearest candidate in range and in front of the player (or right beside them); 0 when there's none.
uint64_t PickInteraction(const InteractViewer& viewer, const InteractCandidate* candidates, size_t count);

// Everything the wrapper needs from the client; tests supply their own.
struct InteractClient
{
    int (*luaType)(void* L, int index);
    const char* (*luaToString)(void* L, int index);
    int (*interactUnit)(void* L);
    bool (*tainted)();
    void (*blocked)();
    uint64_t (*mouseover)();
    bool (*exists)(uint64_t guid);
    uint64_t (*nearest)();
    bool (*interact)(uint64_t guid);
    void (*pushResult)(void* L, bool interacted);
};

// InteractUnit(unit) with "nearest", and "mouseover" falling back to it when the mouse isn't over a unit.
int InteractUnitWithNearest(void* L, const InteractClient& client);

extern const InteractClient g_gameInteractClient;
extern const InteractClient* g_interactClient;
extern void* g_interactUnitResume;   // where the client's function goes on after the moved prologue

extern "C" int __cdecl InteractKeyInteractUnit(void* L);
extern "C" int InteractOriginalInteractUnit();
