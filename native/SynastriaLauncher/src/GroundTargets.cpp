#include <cstring>
#include "Client.h"
#include "GroundTargets.h"
#include "Patch.h"

void* g_castSpellByName = nullptr;
void* g_castSpellByNameResume = nullptr;

GroundTarget ParseGroundTarget(const char* token)
{
    return token == nullptr ? GroundTarget::None
        : _stricmp(token, "cursor") == 0 || _stricmp(token, "cur") == 0 ? GroundTarget::Cursor
        : _stricmp(token, "playerlocation") == 0 || _stricmp(token, "ploc") == 0 ? GroundTarget::PlayerLocation
        : _stricmp(token, "targetlocation") == 0 || _stricmp(token, "tloc") == 0 ? GroundTarget::TargetLocation
        : GroundTarget::None;
}

bool ResolveGroundTarget(GroundTarget target, const GroundClient& client, float* out)
{
    bool found = false;
    if (target == GroundTarget::Cursor && client.mouseOverWorld())
    {
        if (client.refreshHover != nullptr)
        {
            client.refreshHover();
        }
        uint32_t hover = client.hoverType();
        bool ground = hover == 1 || hover == 3;
        if (ground)
        {
            client.hoverPoint(out);
        }
        found = ground || (hover == 2 && client.hoverObjectPosition(out));
    }
    else if (target == GroundTarget::PlayerLocation)
    {
        found = client.playerPosition(out);
    }
    else if (target == GroundTarget::TargetLocation)
    {
        found = client.targetPosition(out);
    }
    return found;
}

int CastSpellByNameWithGround(void* L, const GroundClient& client)
{
    const char* token = client.luaType(L, 2) == Client::LuaTypeString ? client.luaToString(L, 2) : nullptr;
    GroundTarget target = ParseGroundTarget(token);
    int results = 0;
    if (target == GroundTarget::None)
    {
        results = client.castSpellByName(L);
    }
    else
    {
        client.luaSetTop(L, 1);
        results = client.castSpellByName(L);
        if (client.awaitingGround())
        {
            float point[3] = {};
            bool placed = ResolveGroundTarget(target, client, point) && client.placeAt(point);
            if (!placed || client.awaitingGround())
            {
                int top = client.luaGetTop(L);
                client.stopTargeting(L);
                client.luaSetTop(L, top);
            }
        }
    }
    return results;
}

namespace
{
    struct TerrainClick
    {
        uint64_t guid;
        float position[3];
        uint32_t button;
    };

    using LuaTypeFn = int(__cdecl*)(void*, int);
    using LuaToLStringFn = const char*(__cdecl*)(void*, int, size_t*);
    using LuaGetTopFn = int(__cdecl*)(void*);
    using LuaSetTopFn = void(__cdecl*)(void*, int);
    using LuaFunction = int(__cdecl*)(void*);
    using HandleTerrainClickFn = bool(__cdecl*)(TerrainClick*);
    using GetActivePlayerFn = void*(__cdecl*)();
    using ObjectPtrFn = void*(__cdecl*)(uint64_t guid, uint32_t typeMask, const char* file, int line);
    using GetPositionFn = void(__thiscall*)(void* self, float* out);

    template <typename T>
    T Read(uintptr_t address)
    {
        T value;
        memcpy(&value, reinterpret_cast<const void*>(address), sizeof(T));
        return value;
    }

    bool PositionOf(void* object, float* out)
    {
        if (object != nullptr)
        {
            auto* vtable = *static_cast<uintptr_t**>(object);
            reinterpret_cast<GetPositionFn>(vtable[Client::VtableGetPosition / 4])(object, out);
        }
        return object != nullptr;
    }

    void* ObjectByGuid(uint64_t guid, uint32_t typeMask)
    {
        return guid == 0 ? nullptr
            : reinterpret_cast<ObjectPtrFn>(Client::ObjectPtr)(guid, typeMask, reinterpret_cast<const char*>(Client::ObjectPtrFile), 0);
    }

    uintptr_t WorldFrame()
    {
        return Read<uintptr_t>(Client::WorldFrame);
    }

    int GameLuaType(void* L, int index) { return reinterpret_cast<LuaTypeFn>(Client::LuaType)(L, index); }
    const char* GameLuaToString(void* L, int index) { return reinterpret_cast<LuaToLStringFn>(Client::LuaToLString)(L, index, nullptr); }
    int GameLuaGetTop(void* L) { return reinterpret_cast<LuaGetTopFn>(Client::LuaGetTop)(L); }
    void GameLuaSetTop(void* L, int top) { reinterpret_cast<LuaSetTopFn>(Client::LuaSetTop)(L, top); }
    int GameCastSpellByName(void* L) { return reinterpret_cast<LuaFunction>(g_castSpellByName)(L); }
    void GameStopTargeting(void* L) { reinterpret_cast<LuaFunction>(Client::ScriptSpellStopTargeting)(L); }

    bool GameAwaitingGround()
    {
        return Read<uintptr_t>(Client::PendingSpellCast) != 0
            && (Read<uint32_t>(Client::PendingSpellNeeds) & Client::NeedsGroundLocation) != 0;
    }

    bool GamePlaceAt(const float* point)
    {
        TerrainClick click { 0, { point[0], point[1], point[2] }, Client::LeftButton };
        return reinterpret_cast<HandleTerrainClickFn>(Client::HandleTerrainClick)(&click);
    }

    bool GameMouseOverWorld()
    {
        uintptr_t top = Read<uintptr_t>(Client::SimpleTop);
        uintptr_t focus = top != 0 ? Read<uintptr_t>(top + Client::SimpleTopMouseFocus) : 0;
        return WorldFrame() != 0 && focus == WorldFrame();
    }

    using UpdateHoverFn = void(__thiscall*)(void* self);

    void GameRefreshHover()
    {
        reinterpret_cast<UpdateHoverFn>(Client::WorldUpdateHover)(reinterpret_cast<void*>(WorldFrame()));
    }

    uint32_t GameHoverType()
    {
        return Read<uint32_t>(WorldFrame() + Client::WorldHoverType);
    }

    void GameHoverPoint(float* out)
    {
        memcpy(out, reinterpret_cast<const void*>(WorldFrame() + Client::WorldHoverPoint), 3 * sizeof(float));
    }

    bool GameHoverObjectPosition(float* out)
    {
        return PositionOf(ObjectByGuid(Read<uint64_t>(WorldFrame() + Client::WorldHoverGuid), Client::TypeMaskObject), out);
    }

    bool GamePlayerPosition(float* out)
    {
        return PositionOf(reinterpret_cast<GetActivePlayerFn>(Client::GetActivePlayer)(), out);
    }

    bool GameTargetPosition(float* out)
    {
        return PositionOf(ObjectByGuid(Read<uint64_t>(Client::TargetGuid), Client::TypeMaskUnit), out);
    }

    // The function's entry jumps to the wrapper instead; the wrapper reaches the function through
    // GroundOriginalCastSpellByName. Lua keeps the client's own address for CastSpellByName.
    std::string DetourEntry(uint8_t* match, Edit& edit)
    {
        g_castSpellByNameResume = match + Client::ScriptCastSpellByNamePrologue;
        g_castSpellByName = reinterpret_cast<void*>(&GroundOriginalCastSpellByName);
        return JumpTo(0, reinterpret_cast<const void*>(&GroundTargetCastSpellByName), Client::ScriptCastSpellByNamePrologue - 5)(match, edit);
    }
}

// The moved prologue (push ebp; mov ebp, esp; sub esp, 0x2D0), then the rest of the client's function.
extern "C" __declspec(naked) int GroundOriginalCastSpellByName()
{
    __asm
    {
        push ebp
        mov ebp, esp
        sub esp, 0x2D0
        jmp dword ptr [g_castSpellByNameResume]
    }
}

const GroundClient g_gameGroundClient
{
    GameLuaType, GameLuaToString, GameLuaGetTop, GameLuaSetTop, GameCastSpellByName, GameStopTargeting,
    GameAwaitingGround, GamePlaceAt, GameMouseOverWorld, GameHoverType, GameHoverPoint, GameHoverObjectPosition,
    GamePlayerPosition, GameTargetPosition, GameRefreshHover,
};

const GroundClient* g_groundClient = &g_gameGroundClient;

extern "C" int __cdecl GroundTargetCastSpellByName(void* L)
{
    return CastSpellByNameWithGround(L, *g_groundClient);
}

Patch GroundTargetsPatch()
{
    return Patch
    {
        "GroundTargets",
        {
            Code("CastSpellByNameEntry", Hex("55 8B EC 81 EC D0 02 00 00 56 8B 75 08 6A 01 56 E8 3B DC 30 00"), DetourEntry),
        },
        [](const PatchConfig&) { g_castSpellByName = nullptr; g_castSpellByNameResume = nullptr; },
    };
}
