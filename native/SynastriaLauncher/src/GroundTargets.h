#pragma once
#include <cstdint>

// Ground target macro conditionals: "/cast [@cursor] Flamestrike", "[@playerlocation]" and "[@targetlocation]". The
// client's macro option parser passes the unit token through to CastSpellByName, which can't resolve these and
// gives up. CastSpellByName's code jumps to a wrapper first (a detour at its entry, so Lua and other DLLs that
// watch Lua's C functions still see the client's own address): for these tokens it casts with no target, which
// starts ground targeting (the circle) for a ground spell, then places the spell at the chosen point through the
// same function a left click on the ground calls, so the client's own range and placement handling applies. When
// there's no point to use (no target, the mouse over the interface), targeting is cancelled instead. Other targets
// and spells that don't need a ground point are left alone.

enum class GroundTarget
{
    None,
    Cursor,
    PlayerLocation,
    TargetLocation,
};

GroundTarget ParseGroundTarget(const char* token);

// Everything the wrapper needs from the client; tests supply their own.
struct GroundClient
{
    int (*luaType)(void* L, int index);
    const char* (*luaToString)(void* L, int index);
    int (*luaGetTop)(void* L);
    void (*luaSetTop)(void* L, int top);
    int (*castSpellByName)(void* L);
    void (*stopTargeting)(void* L);
    bool (*awaitingGround)();
    bool (*placeAt)(const float* point);
    bool (*mouseOverWorld)();
    uint32_t (*hoverType)();
    void (*hoverPoint)(float* out);
    bool (*hoverObjectPosition)(float* out);
    bool (*playerPosition)(float* out);
    bool (*targetPosition)(float* out);
    void (*refreshHover)();
};

// The point a ground target refers to now; false when there is none.
bool ResolveGroundTarget(GroundTarget target, const GroundClient& client, float* out);

// CastSpellByName(name[, target]) with the ground tokens handled.
int CastSpellByNameWithGround(void* L, const GroundClient& client);

extern const GroundClient g_gameGroundClient;
extern const GroundClient* g_groundClient;
extern void* g_castSpellByName;
extern void* g_castSpellByNameResume;   // where the client's function goes on after the moved prologue

extern "C" int __cdecl GroundTargetCastSpellByName(void* L);
extern "C" int GroundOriginalCastSpellByName();
