#include <cfloat>
#include <cmath>
#include <cstring>
#include "Client.h"
#include "InteractKey.h"
#include "Patch.h"

void* g_interactUnitResume = nullptr;

namespace
{
    constexpr float Pi = 3.14159265f;
}

uint64_t PickInteraction(const InteractViewer& viewer, const InteractCandidate* candidates, size_t count)
{
    uint64_t pick = 0;
    float best = FLT_MAX;
    for (size_t i = 0; i < count; ++i)
    {
        const InteractCandidate& candidate = candidates[i];
        float dx = candidate.position[0] - viewer.position[0];
        float dy = candidate.position[1] - viewer.position[1];
        float dz = candidate.position[2] - viewer.position[2];
        float distance = sqrtf(dx * dx + dy * dy + dz * dz);
        float turn = remainderf(atan2f(dy, dx) - viewer.facing, 2 * Pi);
        bool inReach = distance <= InteractRange + candidate.reach;
        bool ahead = distance <= InteractAnyDirection || fabsf(turn) <= Pi / 2;
        if (inReach && ahead && distance < best)
        {
            best = distance;
            pick = candidate.guid;
        }
    }
    return pick;
}

int InteractUnitWithNearest(void* L, const InteractClient& client)
{
    const char* token = client.luaType(L, 1) == Client::LuaTypeString ? client.luaToString(L, 1) : nullptr;
    bool nearest = token != nullptr
        && (_stricmp(token, "nearest") == 0 || (_stricmp(token, "mouseover") == 0 && !client.exists(client.mouseover())));
    int results = 0;
    if (!nearest)
    {
        results = client.interactUnit(L);
    }
    else if (client.tainted())
    {
        client.blocked();
    }
    else
    {
        uint64_t guid = client.nearest();
        client.pushResult(L, guid != 0 && client.interact(guid));
        results = 1;
    }
    return results;
}

namespace
{
    using LuaTypeFn = int(__cdecl*)(void*, int);
    using LuaToLStringFn = const char*(__cdecl*)(void*, int, size_t*);
    using LuaPushNilFn = void(__cdecl*)(void*);
    using LuaPushNumberFn = void(__cdecl*)(void*, double);
    using GetActivePlayerFn = void*(__cdecl*)();
    using ObjectPtrFn = void*(__cdecl*)(uint64_t guid, uint32_t typeMask, const char* file, int line);
    using EnumVisibleFn = int(__cdecl*)(int(__cdecl*)(uint64_t, void*), void*);
    using GetPositionFn = void(__thiscall*)(void* self, float* out);
    using GetFacingFn = float(__thiscall*)(void* self);
    using CanHoverFn = int(__thiscall*)(void* self);
    using OnInteractFn = int(__cdecl*)(uint64_t guid);
    using ReportBlockedFn = void(__cdecl*)(int, int);
    using UnitCanLootFn = int(__thiscall*)(void* unit, uint32_t now);
    using PlayerCanInteractFn = bool(__thiscall*)(void* player, void* unit);
    using UnitGatherLockFn = uint32_t(__cdecl*)(void* unit);
    using GameObjectCursorFn = int(__thiscall*)(void* object);
    using UsableNowFn = bool(__thiscall*)(void* behavior, int, int, int);

    template <typename T>
    T Read(uintptr_t address)
    {
        T value;
        memcpy(&value, reinterpret_cast<const void*>(address), sizeof(T));
        return value;
    }

    template <typename T>
    T Field(const void* base, uint32_t offset)
    {
        return Read<T>(reinterpret_cast<uintptr_t>(base) + offset);
    }

    template <typename Fn>
    Fn Virtual(void* object, uint32_t offset)
    {
        return reinterpret_cast<Fn>((*static_cast<uintptr_t**>(object))[offset / 4]);
    }

    void* ObjectByGuid(uint64_t guid)
    {
        return guid == 0 ? nullptr
            : reinterpret_cast<ObjectPtrFn>(Client::ObjectPtr)(guid, Client::TypeMaskObject, reinterpret_cast<const char*>(Client::ObjectPtrFile), 0);
    }

    bool UnitInteractable(void* player, void* unit, uint32_t now)
    {
        void* fields = Field<void*>(unit, Client::UnitFields);
        bool alive = Field<int32_t>(fields, Client::UnitFieldHealth) > 0;
        bool skinnable = !alive && (Field<uint32_t>(fields, Client::UnitFieldFlags) & Client::UnitFlagSkinnable) != 0;
        return reinterpret_cast<UnitCanLootFn>(Client::UnitCanLoot)(unit, now) != 0
            || (alive && Field<uint32_t>(fields, Client::UnitFieldNpcFlags) != 0
                && reinterpret_cast<PlayerCanInteractFn>(Client::PlayerCanInteract)(player, unit))
            || (skinnable && reinterpret_cast<UnitGatherLockFn>(Client::UnitGatherLock)(unit) != 0);
    }

    bool GameObjectInteractable(void* object)
    {
        int cursor = reinterpret_cast<GameObjectCursorFn>(Client::GameObjectCursor)(object);
        void* behavior = Field<void*>(object, Client::GameObjectBehavior);
        return (cursor > Client::CursorNone && cursor < Client::CursorFirstUnable)
            || (cursor == Client::CursorCustom && behavior != nullptr
                && Virtual<UsableNowFn>(behavior, Client::VtableUsableNow)(behavior, 0, 0, 0));
    }

    struct Scan
    {
        void* player;
        uint32_t now;
        InteractCandidate found[256];
        size_t count;
    };

    Scan g_scan;

    int __cdecl Collect(uint64_t guid, void* context)
    {
        auto* scan = static_cast<Scan*>(context);
        void* object = ObjectByGuid(guid);
        uint32_t type = object != nullptr ? Field<uint32_t>(object, Client::ObjectTypeId) : 0;
        bool known = (type == Client::TypeUnit || type == Client::TypeGameObject) && object != scan->player
            && Virtual<CanHoverFn>(object, Client::VtableCanHover)(object) != 0;
        bool interactable = known
            && (type == Client::TypeUnit ? UnitInteractable(scan->player, object, scan->now) : GameObjectInteractable(object));
        if (interactable && scan->count < sizeof(scan->found) / sizeof(scan->found[0]))
        {
            InteractCandidate& candidate = scan->found[scan->count++];
            candidate.guid = guid;
            Virtual<GetPositionFn>(object, Client::VtableGetPosition)(object, candidate.position);
            candidate.reach = type == Client::TypeUnit
                ? Field<float>(Field<void*>(object, Client::UnitFields), Client::UnitFieldCombatReach) : 0.0f;
        }
        return 1;
    }

    int GameLuaType(void* L, int index) { return reinterpret_cast<LuaTypeFn>(Client::LuaType)(L, index); }
    const char* GameLuaToString(void* L, int index) { return reinterpret_cast<LuaToLStringFn>(Client::LuaToLString)(L, index, nullptr); }
    int GameInteractUnit(void* L) { return reinterpret_cast<int(__cdecl*)(void*)>(&InteractOriginalInteractUnit)(L); }
    bool GameTainted() { return Read<uint32_t>(Client::TaintSource) != 0; }
    void GameBlocked() { reinterpret_cast<ReportBlockedFn>(Client::ReportBlockedAction)(0, 0); }
    uint64_t GameMouseover() { return Read<uint64_t>(Client::MouseoverGuid); }
    bool GameExists(uint64_t guid) { return ObjectByGuid(guid) != nullptr; }
    bool GameInteract(uint64_t guid) { return reinterpret_cast<OnInteractFn>(Client::OnInteract)(guid) != 0; }

    void GamePushResult(void* L, bool interacted)
    {
        if (interacted)
        {
            reinterpret_cast<LuaPushNumberFn>(Client::LuaPushNumber)(L, 1.0);
        }
        else
        {
            reinterpret_cast<LuaPushNilFn>(Client::LuaPushNil)(L);
        }
    }

    uint64_t GameNearest()
    {
        void* player = reinterpret_cast<GetActivePlayerFn>(Client::GetActivePlayer)();
        uint64_t pick = 0;
        if (player != nullptr && Read<uint8_t>(Client::IsInWorld) != 0)
        {
            g_scan.player = player;
            g_scan.now = Read<uint32_t>(Client::GameTimeMs);
            g_scan.count = 0;
            reinterpret_cast<EnumVisibleFn>(Client::EnumVisibleObjects)(Collect, &g_scan);
            InteractViewer viewer {};
            Virtual<GetPositionFn>(player, Client::VtableGetPosition)(player, viewer.position);
            viewer.facing = Virtual<GetFacingFn>(player, Client::VtableGetFacing)(player);
            pick = PickInteraction(viewer, g_scan.found, g_scan.count);
        }
        return pick;
    }

    // The function's entry jumps to the wrapper instead; the wrapper reaches the function through
    // InteractOriginalInteractUnit. Lua keeps the client's own address for InteractUnit.
    std::string DetourEntry(uint8_t* match, Edit& edit)
    {
        g_interactUnitResume = match + Client::ScriptInteractUnitPrologue;
        return JumpTo(0, reinterpret_cast<const void*>(&InteractKeyInteractUnit), Client::ScriptInteractUnitPrologue - 5)(match, edit);
    }
}

// The moved prologue (push ebp; mov ebp, esp; push edi; mov edi, [ebp+8]), then the rest of the client's function.
extern "C" __declspec(naked) int InteractOriginalInteractUnit()
{
    __asm
    {
        push ebp
        mov ebp, esp
        push edi
        mov edi, [ebp + 8]
        jmp dword ptr [g_interactUnitResume]
    }
}

const InteractClient g_gameInteractClient
{
    GameLuaType, GameLuaToString, GameInteractUnit, GameTainted, GameBlocked, GameMouseover, GameExists, GameNearest,
    GameInteract, GamePushResult,
};

const InteractClient* g_interactClient = &g_gameInteractClient;

extern "C" int __cdecl InteractKeyInteractUnit(void* L)
{
    return InteractUnitWithNearest(L, *g_interactClient);
}

Patch InteractKeyPatch()
{
    return Patch
    {
        "InteractKey",
        {
            Code("InteractUnitEntry", Hex("55 8B EC 57 8B 7D 08 6A 01 57 E8 51 60 32 00 83 C4 08 85 C0 75 13"), DetourEntry),
        },
        [](const PatchConfig&) { g_interactUnitResume = nullptr; },
    };
}
