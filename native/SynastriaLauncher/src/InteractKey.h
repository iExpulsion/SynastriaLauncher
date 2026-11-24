#pragma once
#include <cstddef>
#include <cstdint>

// Interact Key: InteractUnit("nearest") interacts with the nearest thing in front of the player. A key binding of its
// own (Key Bindings > Synastria Launcher > Interact With Nearest) and a /interact macro command, which takes the
// usual conditions ("/interact [@mouseover]"), call it. InteractUnit's entry jumps to a wrapper (a detour, like
// Ground Target Macros) that keeps the function's protection: from tainted code it reports a blocked action, as the
// client does. The binding and command are added from a call wrapped around the game UI's load of its own
// Bindings.xml, so they exist before saved keys are read, are untainted, and come back on every UI reload. What
// counts as interactable follows the client's hover cursor logic: a lootable corpse, a friendly NPC with something to
// offer, a corpse the player's gathering skill is high enough for, or a game object whose cursor isn't an "unable"
// one. The interaction goes through the client's right-click path. Pressing again on loot just opened (a corpse or
// chest) moves on to the next one not opened lately, so repeated presses work through everything lootable nearby.

struct InteractCandidate
{
    uint64_t guid;
    float position[3];
    float reach;        // the unit's combat reach, added to the range
    bool loot;          // a lootable corpse or chest
    uint32_t opened;    // for loot opened lately, how recently: higher is more recent; 0 when not
};

struct InteractViewer
{
    float position[3];
    float facing;
};

constexpr float InteractRange = 6.0f;          // yards from the object's position, plus its reach
constexpr float InteractAnyDirection = 2.5f;   // closer than this counts whatever way the player faces
constexpr uint32_t InteractRecentLootMs = 30000;

// The nearest candidate in range and in front of the player (or right beside them); 0 when there's none. When
// that's loot opened lately, the reachable loot opened longest ago (or never) is taken instead, nearest first.
uint64_t PickInteraction(const InteractViewer& viewer, const InteractCandidate* candidates, size_t count);

// The skill a corpse of this level needs for skinning, herbalism, mining or engineering, as its tooltip works it out.
int GatherSkillNeeded(int level);

// Remembers loot as it's opened and says how recently each was; the oldest is forgotten when it's full.
class RecentLoot
{
public:
    void Opened(uint64_t guid, uint32_t now);
    uint32_t Recency(uint64_t guid, uint32_t now) const;

private:
    struct Entry
    {
        uint64_t guid = 0;
        uint32_t at = 0;
        uint32_t order = 0;
    };

    Entry m_entries[16];
    uint32_t m_next = 1;
};

// Everything the wrapper needs from the client; tests supply their own.
struct InteractClient
{
    int (*luaType)(void* L, int index);
    const char* (*luaToString)(void* L, int index);
    int (*interactUnit)(void* L);
    bool (*tainted)();
    void (*blocked)();
    uint64_t (*nearest)();
    bool (*interact)(uint64_t guid);
    void (*pushResult)(void* L, bool interacted);
};

// InteractUnit(unit) with "nearest" added.
int InteractUnitWithNearest(void* L, const InteractClient& client);

// The key binding, as Bindings.xml text, and the Lua that names it and adds /interact.
extern const char* const InteractBindingsXml;
extern const char* const InteractSetupScript;

extern const InteractClient g_gameInteractClient;
extern const InteractClient* g_interactClient;
extern void* g_interactUnitResume;   // where the client's function goes on after the moved prologue
extern void* g_loadBindingsFile;     // the client's bindings file loader, called through by the wrapper

extern "C" int __cdecl InteractKeyInteractUnit(void* L);
extern "C" int InteractOriginalInteractUnit();
int __fastcall InteractKeyLoadBindings(void* bindings, void* unused, const char* file, void* hash, void* status);
