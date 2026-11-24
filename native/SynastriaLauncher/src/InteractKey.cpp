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

namespace
{
    // The candidate's distance, or a negative one when it's out of reach or behind the player.
    float Reachable(const InteractViewer& viewer, const InteractCandidate& candidate)
    {
        float dx = candidate.position[0] - viewer.position[0];
        float dy = candidate.position[1] - viewer.position[1];
        float dz = candidate.position[2] - viewer.position[2];
        float distance = sqrtf(dx * dx + dy * dy + dz * dz);
        float turn = remainderf(atan2f(dy, dx) - viewer.facing, 2 * Pi);
        bool inReach = distance <= InteractRange + candidate.reach;
        bool ahead = distance <= InteractAnyDirection || fabsf(turn) <= Pi / 2;
        return inReach && ahead ? distance : -1.0f;
    }
}

uint64_t PickInteraction(const InteractViewer& viewer, const InteractCandidate* candidates, size_t count)
{
    const InteractCandidate* nearest = nullptr;
    float best = FLT_MAX;
    for (size_t i = 0; i < count; ++i)
    {
        float distance = Reachable(viewer, candidates[i]);
        if (distance >= 0 && distance < best)
        {
            best = distance;
            nearest = &candidates[i];
        }
    }
    const InteractCandidate* pick = nearest;
    if (nearest != nullptr && nearest->loot && nearest->opened != 0)
    {
        uint32_t oldest = UINT32_MAX;
        float closest = FLT_MAX;
        for (size_t i = 0; i < count; ++i)
        {
            const InteractCandidate& candidate = candidates[i];
            float distance = candidate.loot ? Reachable(viewer, candidate) : -1.0f;
            bool better = candidate.opened < oldest || (candidate.opened == oldest && distance < closest);
            if (distance >= 0 && better)
            {
                oldest = candidate.opened;
                closest = distance;
                pick = &candidate;
            }
        }
    }
    return pick != nullptr ? pick->guid : 0;
}

int GatherSkillNeeded(int level)
{
    return level <= 10 ? 1 : level < 20 ? (level - 10) * 10 : level * 5;
}

void RecentLoot::Opened(uint64_t guid, uint32_t now)
{
    Entry* slot = &m_entries[0];
    for (Entry& entry : m_entries)
    {
        if (entry.guid == guid)
        {
            slot = &entry;
            break;
        }
        if (entry.order < slot->order)
        {
            slot = &entry;
        }
    }
    *slot = Entry { guid, now, m_next++ };
}

uint32_t RecentLoot::Recency(uint64_t guid, uint32_t now) const
{
    for (const Entry& entry : m_entries)
    {
        if (entry.order != 0 && entry.guid == guid && now - entry.at < InteractRecentLootMs)
        {
            return entry.order;
        }
    }
    return 0;
}

int InteractUnitWithNearest(void* L, const InteractClient& client)
{
    const char* token = client.luaType(L, 1) == Client::LuaTypeString ? client.luaToString(L, 1) : nullptr;
    bool nearest = token != nullptr && _stricmp(token, "nearest") == 0;
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
    using UnitGatherTypeFn = int(__thiscall*)(void* unit);
    using SkillIndexFn = int(__thiscall*)(void* player, uint32_t skillLine);
    using SkillValueFn = int(__thiscall*)(void* player, int index);
    using GetTimeMsFn = uint32_t(__cdecl*)();

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

    enum class Verdict
    {
        None,
        Interact,
        Loot,
    };

    bool CanGather(void* player, void* unit, const void* fields)
    {
        int type = reinterpret_cast<UnitGatherTypeFn>(Client::UnitGatherType)(unit);
        bool known = type >= 0 && type < static_cast<int>(sizeof(Client::GatherSkills) / sizeof(Client::GatherSkills[0]));
        int index = known ? reinterpret_cast<SkillIndexFn>(Client::PlayerSkillIndex)(player, Client::GatherSkills[type]) : -1;
        int skill = index >= 0 ? reinterpret_cast<SkillValueFn>(Client::PlayerSkillValue)(player, index) : 0;
        return skill > 0 && skill >= GatherSkillNeeded(Field<int32_t>(fields, Client::UnitFieldLevel));
    }

    Verdict JudgeUnit(void* player, void* unit, uint32_t now)
    {
        void* fields = Field<void*>(unit, Client::UnitFields);
        bool alive = Field<int32_t>(fields, Client::UnitFieldHealth) > 0;
        bool skinnable = !alive && (Field<uint32_t>(fields, Client::UnitFieldFlags) & Client::UnitFlagSkinnable) != 0;
        bool loot = reinterpret_cast<UnitCanLootFn>(Client::UnitCanLoot)(unit, now) != 0;
        bool talk = alive && Field<uint32_t>(fields, Client::UnitFieldNpcFlags) != 0
            && reinterpret_cast<PlayerCanInteractFn>(Client::PlayerCanInteract)(player, unit);
        bool gather = skinnable && reinterpret_cast<UnitGatherLockFn>(Client::UnitGatherLock)(unit) != 0
            && CanGather(player, unit, fields);
        return loot ? Verdict::Loot : talk || gather ? Verdict::Interact : Verdict::None;
    }

    Verdict JudgeGameObject(void* object)
    {
        int cursor = reinterpret_cast<GameObjectCursorFn>(Client::GameObjectCursor)(object);
        void* behavior = Field<void*>(object, Client::GameObjectBehavior);
        bool usable = (cursor > Client::CursorNone && cursor < Client::CursorFirstUnable)
            || (cursor == Client::CursorCustom && behavior != nullptr
                && Virtual<UsableNowFn>(behavior, Client::VtableUsableNow)(behavior, 0, 0, 0));
        bool loot = cursor == Client::CursorPickup || cursor == Client::CursorLootAll;
        return loot ? Verdict::Loot : usable ? Verdict::Interact : Verdict::None;
    }

    struct Scan
    {
        void* player;
        uint32_t now;
        uint32_t clock;
        InteractCandidate found[256];
        size_t count;
    };

    Scan g_scan;
    RecentLoot g_recentLoot;

    int __cdecl Collect(uint64_t guid, void* context)
    {
        auto* scan = static_cast<Scan*>(context);
        void* object = ObjectByGuid(guid);
        uint32_t type = object != nullptr ? Field<uint32_t>(object, Client::ObjectTypeId) : 0;
        bool known = (type == Client::TypeUnit || type == Client::TypeGameObject) && object != scan->player
            && Virtual<CanHoverFn>(object, Client::VtableCanHover)(object) != 0;
        Verdict verdict = !known ? Verdict::None
            : type == Client::TypeUnit ? JudgeUnit(scan->player, object, scan->now) : JudgeGameObject(object);
        if (verdict != Verdict::None && scan->count < sizeof(scan->found) / sizeof(scan->found[0]))
        {
            InteractCandidate& candidate = scan->found[scan->count++];
            candidate.guid = guid;
            Virtual<GetPositionFn>(object, Client::VtableGetPosition)(object, candidate.position);
            candidate.reach = type == Client::TypeUnit
                ? Field<float>(Field<void*>(object, Client::UnitFields), Client::UnitFieldCombatReach) : 0.0f;
            candidate.loot = verdict == Verdict::Loot;
            candidate.opened = candidate.loot ? g_recentLoot.Recency(guid, scan->clock) : 0;
        }
        return 1;
    }

    int GameLuaType(void* L, int index) { return reinterpret_cast<LuaTypeFn>(Client::LuaType)(L, index); }
    const char* GameLuaToString(void* L, int index) { return reinterpret_cast<LuaToLStringFn>(Client::LuaToLString)(L, index, nullptr); }
    int GameInteractUnit(void* L) { return reinterpret_cast<int(__cdecl*)(void*)>(&InteractOriginalInteractUnit)(L); }
    bool GameTainted() { return Read<uint32_t>(Client::TaintSource) != 0; }
    void GameBlocked() { reinterpret_cast<ReportBlockedFn>(Client::ReportBlockedAction)(0, 0); }
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
            g_scan.clock = reinterpret_cast<GetTimeMsFn>(Client::GetTimeMs)();
            g_scan.count = 0;
            reinterpret_cast<EnumVisibleFn>(Client::EnumVisibleObjects)(Collect, &g_scan);
            InteractViewer viewer {};
            Virtual<GetPositionFn>(player, Client::VtableGetPosition)(player, viewer.position);
            viewer.facing = Virtual<GetFacingFn>(player, Client::VtableGetFacing)(player);
            pick = PickInteraction(viewer, g_scan.found, g_scan.count);
        }
        for (size_t i = 0; i < g_scan.count && pick != 0; ++i)
        {
            if (g_scan.found[i].guid == pick && g_scan.found[i].loot)
            {
                g_recentLoot.Opened(pick, g_scan.clock);
            }
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
    GameLuaType, GameLuaToString, GameInteractUnit, GameTainted, GameBlocked, GameNearest, GameInteract, GamePushResult,
};

const char* const InteractBindingsXml =
    "<Bindings>"
    "<Binding name=\"SYNASTRIALAUNCHER_INTERACT\" header=\"SYNASTRIALAUNCHER\">InteractUnit(\"nearest\")</Binding>"
    "</Bindings>";

const char* const InteractSetupScript = R"lua(
BINDING_HEADER_SYNASTRIALAUNCHER = "Synastria Launcher"
BINDING_NAME_SYNASTRIALAUNCHER_INTERACT = "Interact With Nearest"
if SlashCmdList then
    SLASH_SYNASTRIALAUNCHER_INTERACT1 = "/interact"
    SlashCmdList["SYNASTRIALAUNCHER_INTERACT"] = function(msg)
        local action, target = SecureCmdOptionParse(msg or "")
        if action then
            InteractUnit(target or (action ~= "" and action) or "nearest")
        end
    end
end
)lua";

void* g_loadBindingsFile = nullptr;

namespace
{
    using LoadBindingsFileFn = int(__thiscall*)(void* bindings, const char* file, void* hash, void* status);
    using LoadBindingNodeFn = int(__thiscall*)(void* bindings, const char* file, void* node, void* status);
    using XmlParseFn = void*(__cdecl*)(const char* text, uint32_t length);
    using XmlFreeFn = void(__cdecl*)(void* tree);
    using ExecuteFn = void(__cdecl*)(const char* code, const char* chunk, int);

    void AddOwnBindings(void* bindings, void* status)
    {
        void* tree = reinterpret_cast<XmlParseFn>(Client::XmlParse)(InteractBindingsXml, static_cast<uint32_t>(strlen(InteractBindingsXml)));
        if (tree != nullptr)
        {
            void* root = *static_cast<void**>(tree);
            for (void* node = Field<void*>(root, Client::XmlFirstChild); node != nullptr; node = Field<void*>(node, Client::XmlNextSibling))
            {
                reinterpret_cast<LoadBindingNodeFn>(Client::LoadBindingNode)(bindings, "SynastriaLauncher.xml", node, status);
            }
            reinterpret_cast<XmlFreeFn>(Client::XmlFree)(tree);
        }
        reinterpret_cast<ExecuteFn>(Client::FrameScriptExecute)(InteractSetupScript, "SynastriaLauncher", 0);
    }
}

// Stands in for the game UI's call that loads Interface\FrameXML\Bindings.xml: that file first, then ours.
int __fastcall InteractKeyLoadBindings(void* bindings, void*, const char* file, void* hash, void* status)
{
    int loaded = reinterpret_cast<LoadBindingsFileFn>(g_loadBindingsFile)(bindings, file, hash, status);
    AddOwnBindings(bindings, status);
    return loaded;
}

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
            Code("LoadFrameXmlBindings", Hex("8D 45 E4 50 8D 8D 44 FF FF FF 51 8B 0D D8 AD BE 00 68 EC 2F A0 00 E8 B6 9A 03 00"),
                CallTo(22, reinterpret_cast<const void*>(&InteractKeyLoadBindings), &g_loadBindingsFile)),
        },
        [](const PatchConfig&) { g_interactUnitResume = nullptr; g_loadBindingsFile = nullptr; },
    };
}
