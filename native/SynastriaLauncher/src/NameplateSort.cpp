#include "Client.h"
#include "NameplateSort.h"
#include "Patch.h"

void* g_fireOnUpdate = nullptr;

void OrderFarthestFirst(SortEntry* entries, int count)
{
    for (int i = 1; i < count; ++i)
    {
        SortEntry current = entries[i];
        int j = i;
        while (j > 0 && entries[j - 1].key < current.key)
        {
            entries[j] = entries[j - 1];
            --j;
        }
        entries[j] = current;
    }
}

namespace
{
    struct Pass
    {
        const SortClient* client;
        float player[3];
        uint64_t target;
        SortEntry entries[MaxSortedPlates];
        int count;
    };

    Pass g_pass;

    int __cdecl CollectUnit(uint64_t guid, void* context)
    {
        Pass& pass = *static_cast<Pass*>(context);
        void* unit = pass.client->unitByGuid(guid);
        void* plate = unit != nullptr ? pass.client->nameplateOf(unit) : nullptr;
        if (plate != nullptr && pass.count < MaxSortedPlates)
        {
            float key = -1.0f;
            if (guid != pass.target)
            {
                float at[3];
                pass.client->position(unit, at);
                float dx = at[0] - pass.player[0];
                float dy = at[1] - pass.player[1];
                float dz = at[2] - pass.player[2];
                key = dx * dx + dy * dy + dz * dz;
            }
            pass.entries[pass.count++] = { plate, key };
        }
        return 1;
    }

    // The client's side of the pass.
    using GetActivePlayerFn = void*(__cdecl*)();
    using EnumVisibleFn = int(__cdecl*)(int(__cdecl*)(uint64_t, void*), void*);
    using ObjectPtrFn = void*(__cdecl*)(uint64_t guid, uint32_t typeMask, const char* file, int line);
    using GetPositionFn = void(__thiscall*)(void* self, float* out);
    using SetFrameLevelFn = void(__thiscall*)(void* self, int level, int flag);

    bool GameInWorld()
    {
        return *reinterpret_cast<const uint8_t*>(Client::IsInWorld) != 0;
    }

    void* GameActivePlayer()
    {
        return reinterpret_cast<GetActivePlayerFn>(Client::GetActivePlayer)();
    }

    void GamePosition(void* object, float* out)
    {
        auto* vtable = *static_cast<uintptr_t**>(object);
        reinterpret_cast<GetPositionFn>(vtable[Client::VtableGetPosition / 4])(object, out);
    }

    void GameEnumVisible(int(__cdecl* callback)(uint64_t, void*), void* context)
    {
        reinterpret_cast<EnumVisibleFn>(Client::EnumVisibleObjects)(callback, context);
    }

    void* GameUnitByGuid(uint64_t guid)
    {
        return reinterpret_cast<ObjectPtrFn>(Client::ObjectPtr)(guid, Client::TypeMaskUnit,
            reinterpret_cast<const char*>(Client::ObjectPtrFile), 0);
    }

    uint64_t GameTargetGuid()
    {
        return *reinterpret_cast<const uint64_t*>(Client::TargetGuid);
    }

    void* GameNameplateOf(void* unit)
    {
        return *reinterpret_cast<void**>(static_cast<uint8_t*>(unit) + Client::UnitNameplate);
    }

    void GameSetLevel(void* plate, int level)
    {
        reinterpret_cast<SetFrameLevelFn>(Client::SetFrameLevel)(plate, level, 1);
    }
}

const SortClient g_gameSortClient
{
    GameInWorld, GameActivePlayer, GamePosition, GameEnumVisible, GameUnitByGuid, GameTargetGuid, GameNameplateOf, GameSetLevel,
};

const SortClient* g_sortClient = &g_gameSortClient;

void SortNameplates(const SortClient& client)
{
    void* player = client.inWorld() ? client.activePlayer() : nullptr;
    if (player != nullptr)
    {
        g_pass.client = &client;
        g_pass.count = 0;
        g_pass.target = client.targetGuid();
        client.position(player, g_pass.player);
        client.enumVisible(&CollectUnit, &g_pass);
        OrderFarthestFirst(g_pass.entries, g_pass.count);
        for (int i = 0; i < g_pass.count; ++i)
        {
            client.setLevel(g_pass.entries[i].plate, FirstPlateLevel + i);
        }
    }
}

namespace
{
    void __cdecl RunSortPass()
    {
        SortNameplates(*g_sortClient);
    }
}

extern "C" __declspec(naked) void SortThenFireOnUpdate()
{
    __asm
    {
        pushad
        pushfd
        call RunSortPass
        popfd
        popad
        jmp dword ptr [g_fireOnUpdate]
    }
}

// The registration pushes FireOnUpdate's address; it pushes the wrapper instead. The client's own per-plate level
// code (SetFrameLevel 20 for the target, 10 for the others) would undo the order: "mov eax, [esi+2A8h]" (the plate's
// unit GUID, compared with the target's) becomes "push edi" (which that code did next) and a short jump to just past
// its SetFrameLevel call.
Patch NameplateSortPatch()
{
    return Patch
    {
        "NameplateSort",
        {
            Code("RegisterFireOnUpdate", Hex("D9 E8 8D 46 74 50") + Hex("68") + U32(Client::FireOnUpdate) + Hex("57 6A 04"),
                PushAddress(6, reinterpret_cast<const void*>(&SortThenFireOnUpdate), &g_fireOnUpdate)),
            Swap("SkipPlateLevels",
                Hex("8B F1") + Hex("8B 86 A8 02 00 00") + Hex("3B 05") + U32(Client::TargetGuid) + Hex("57 75 14"),
                Hex("8B F1") + Hex("57 EB 2B 02 00 00") + Hex("3B 05") + U32(Client::TargetGuid) + Hex("57 75 14")),
        },
        [](const PatchConfig&) { g_fireOnUpdate = nullptr; },
    };
}
