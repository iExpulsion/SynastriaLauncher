#pragma once
#include <cstdint>

// Draws nearer nameplates over farther ones, with the target always on top: once a frame, before the client's
// FrameScript_FireOnUpdate, every visible unit with a nameplate gets frame level 10, 11, ... from farthest to nearest,
// and the target last. The client's own per-plate level code (target 20, others 10) is switched off by the patch.

constexpr int MaxSortedPlates = 64;
constexpr int FirstPlateLevel = 10;

struct SortEntry
{
    void* plate;
    float key;
};

// Orders entries farthest first (largest key first), keeping the order of equal keys. The target's key is -1.
void OrderFarthestFirst(SortEntry* entries, int count);

// What the sort pass needs from the client; tests supply their own.
struct SortClient
{
    bool (*inWorld)();
    void* (*activePlayer)();
    void (*position)(void* object, float* out);
    void (*enumVisible)(int(__cdecl* callback)(uint64_t guid, void* context), void* context);
    void* (*unitByGuid)(uint64_t guid);
    uint64_t (*targetGuid)();
    void* (*nameplateOf)(void* unit);
    void (*setLevel)(void* plate, int level);
};

extern const SortClient g_gameSortClient;
extern const SortClient* g_sortClient;
extern void* g_fireOnUpdate;

void SortNameplates(const SortClient& client);

// Registered in place of FireOnUpdate: runs the sort pass with every register kept, then goes on to the original.
extern "C" void SortThenFireOnUpdate();
