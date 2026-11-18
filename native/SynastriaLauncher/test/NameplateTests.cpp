// Unit tests for Nameplate Sorting and Nameplate Stacking: the ordering and layout logic, full passes against a mock
// client, and the assembly shims called the way the client calls them.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cmath>
#include <cstdio>
#include <string>
#include <utility>
#include <vector>
#include "../src/NameplateSort.h"
#include "../src/NameplateStack.h"
#include "NativeTests.h"

#pragma warning(disable : 4731)

namespace
{
    // ---- Mock client for the sort pass ----

    struct MockUnit
    {
        uint64_t guid;
        float position[3];
        intptr_t plate;
    };

    std::vector<MockUnit> s_units;
    bool s_inWorld = true;
    bool s_hasPlayer = true;
    int s_player = 0;
    float s_playerPosition[3] = {};
    uint64_t s_target = 0;
    std::vector<std::pair<intptr_t, int>> s_levels;

    bool MockInWorld() { return s_inWorld; }
    void* MockActivePlayer() { return s_hasPlayer ? &s_player : nullptr; }

    void MockPosition(void* object, float* out)
    {
        const float* from = object == &s_player ? s_playerPosition : static_cast<MockUnit*>(object)->position;
        out[0] = from[0];
        out[1] = from[1];
        out[2] = from[2];
    }

    void MockEnumVisible(int(__cdecl* callback)(uint64_t, void*), void* context)
    {
        for (const MockUnit& unit : s_units)
        {
            callback(unit.guid, context);
        }
    }

    void* MockUnitByGuid(uint64_t guid)
    {
        for (MockUnit& unit : s_units)
        {
            if (unit.guid == guid && unit.plate >= 0)
            {
                return &unit;
            }
        }
        return nullptr;
    }

    uint64_t MockTargetGuid() { return s_target; }
    void* MockNameplateOf(void* unit) { return reinterpret_cast<void*>(static_cast<MockUnit*>(unit)->plate); }
    void MockSetLevel(void* plate, int level) { s_levels.push_back({ reinterpret_cast<intptr_t>(plate), level }); }

    const SortClient s_mockSortClient
    {
        MockInWorld, MockActivePlayer, MockPosition, MockEnumVisible, MockUnitByGuid, MockTargetGuid, MockNameplateOf, MockSetLevel,
    };

    void ResetSortWorld()
    {
        s_inWorld = true;
        s_hasPlayer = true;
        s_playerPosition[0] = 100;
        s_playerPosition[1] = 200;
        s_playerPosition[2] = 10;
        s_target = 0x103;
        s_levels.clear();
        s_units = {
            { 0x101, { 110, 200, 10 }, 0x101 },
            { 0x102, { 100, 260, 10 }, 0x102 },
            { 0x103, { 130, 230, 10 }, 0x103 },
            { 0x104, { 100, 205, 10 }, 0x104 },
        };
    }

    std::string Levels()
    {
        std::string text;
        for (const auto& [plate, level] : s_levels)
        {
            char part[32];
            snprintf(part, sizeof(part), "%s%llx:%d", text.empty() ? "" : " ", static_cast<long long>(plate), level);
            text += part;
        }
        return text;
    }

    uint32_t s_seen[3];

    extern "C" __declspec(naked) void FakeOriginal()
    {
        __asm
        {
            mov dword ptr [s_seen], eax
            mov dword ptr [s_seen + 4], ecx
            mov dword ptr [s_seen + 8], edx
            ret
        }
    }

    // Calls a wrapper shim with marked registers; true when the original was reached with them unchanged and the
    // callee-saved ones came back.
    bool CallWrapper(void (*shim)())
    {
        s_seen[0] = s_seen[1] = s_seen[2] = 0;
        uint32_t kept = 0;
        __asm
        {
            push ebx
            push esi
            push edi
            mov edi, shim
            mov ebx, 0B0B0B0B0h
            mov esi, 5E5E5E5Eh
            mov eax, 11111111h
            mov ecx, 22222222h
            mov edx, 33333333h
            call edi
            xor eax, eax
            cmp ebx, 0B0B0B0B0h
            jne bad
            cmp esi, 5E5E5E5Eh
            jne bad
            mov eax, 1
        bad:
            pop edi
            pop esi
            pop ebx
            mov kept, eax
        }
        return kept == 1 && s_seen[0] == 0x11111111 && s_seen[1] == 0x22222222 && s_seen[2] == 0x33333333;
    }

    void SortTests()
    {
        SortEntry entries[] = { { nullptr, 100 }, { nullptr, 3600 }, { nullptr, -1 }, { nullptr, 25 }, { nullptr, 100 } };
        entries[0].plate = reinterpret_cast<void*>(1);
        entries[4].plate = reinterpret_cast<void*>(5);
        OrderFarthestFirst(entries, 5);
        // Largest key first, equal keys keep their order, the target's -1 last.
        Check(entries[0].key == 3600 && entries[1].plate == reinterpret_cast<void*>(1) && entries[2].plate == reinterpret_cast<void*>(5)
            && entries[3].key == 25 && entries[4].key == -1, "sort order: farthest first, ties stable, target last");

        // A full pass sets levels 10, 11, ... from farthest to nearest, with the target on top.
        ResetSortWorld();
        SortNameplates(s_mockSortClient);
        Check(Levels() == "102:10 101:11 104:12 103:13", "pass levels farthest first, target last: " + Levels());

        // Nothing happens outside the world or without a player.
        ResetSortWorld();
        s_inWorld = false;
        SortNameplates(s_mockSortClient);
        Check(s_levels.empty(), "no levels outside the world");
        ResetSortWorld();
        s_hasPlayer = false;
        SortNameplates(s_mockSortClient);
        Check(s_levels.empty(), "no levels without a player");

        // Units without a nameplate, or that can't be looked up, are left out.
        ResetSortWorld();
        s_units[0].plate = 0;
        s_units[1].plate = -1;
        SortNameplates(s_mockSortClient);
        Check(Levels() == "104:10 103:11", "units without a plate or object are skipped: " + Levels());

        // At most 64 plates are ordered.
        ResetSortWorld();
        s_units.clear();
        for (int i = 0; i < 70; ++i)
        {
            s_units.push_back({ 0x200u + i, { 100.0f + i, 200, 10 }, 0x200 + i });
        }
        SortNameplates(s_mockSortClient);
        Check(s_levels.size() == MaxSortedPlates && s_levels.front().second == 10 && s_levels.back().second == 73,
            "70 units: 64 levels, 10 to 73");

        // The FireOnUpdate wrapper runs the pass, then reaches the original with every register as it was.
        ResetSortWorld();
        g_sortClient = &s_mockSortClient;
        g_fireOnUpdate = reinterpret_cast<void*>(&FakeOriginal);
        Check(CallWrapper(&SortThenFireOnUpdate) && s_levels.size() == 4, "FireOnUpdate wrapper keeps registers and runs the pass");
        g_sortClient = &g_gameSortClient;
    }

    // ---- Stacking ----

    constexpr float Gap = 0.03f;

    std::vector<PlateBox> Column()
    {
        return {
            { reinterpret_cast<void*>(1), 0.5f, 0.50f, 0.1f, 0.03f },
            { reinterpret_cast<void*>(2), 0.5f, 0.51f, 0.1f, 0.03f },
            { reinterpret_cast<void*>(3), 0.5f, 0.52f, 0.1f, 0.03f },
        };
    }

    bool Near(float a, float b)
    {
        return std::fabs(a - b) < 1e-5f;
    }

    std::vector<PlateBox> s_plates;
    uint32_t s_now = 0;

    int MockCollect(PlateBox* out, int capacity)
    {
        int count = 0;
        for (const PlateBox& plate : s_plates)
        {
            if (count < capacity)
            {
                out[count++] = plate;
            }
        }
        return count;
    }

    uint32_t MockNow() { return s_now; }

    const StackClient s_mockStackClient { MockCollect, MockNow };

    float s_loadedY = 0;
    uint32_t s_pushed = 0;
    uint32_t s_eaxAfter = 0;

    // Runs LoadStackedY as the layout loop does: ebp on its frame with the raw y at [ebp-8], ebx the plate.
    float CallLoadStackedY(void* plate, float rawY, uint32_t& pushed, bool& eaxKept)
    {
        float frame[4] = { 0, rawY, 0, 0 };
        float* fakeEbp = &frame[3];
        __asm
        {
            mov ecx, fakeEbp
            mov edx, plate
            push ebx
            push ebp
            mov ebx, edx
            mov ebp, ecx
            mov eax, 0A5A5A5A5h
            call LoadStackedY
            fstp dword ptr [s_loadedY]
            pop dword ptr [s_pushed]
            mov dword ptr [s_eaxAfter], eax
            pop ebp
            pop ebx
        }
        pushed = s_pushed;
        eaxKept = s_eaxAfter == 0xA5A5A5A5;
        return s_loadedY;
    }

    uint32_t s_world[0xB20 / 4];

    uint32_t CallFlagFix(uint32_t flags, bool moving)
    {
        s_world[0xB10 / 4] = flags;
        g_stackMoving = moving ? 1 : 0;
        uint32_t* world = s_world;
        __asm
        {
            push esi
            mov esi, world
            call ClearLayoutFlagUnlessMoving
            pop esi
        }
        return *static_cast<volatile uint32_t*>(&s_world[0xB10 / 4]);
    }

    void StackTests()
    {
        // A column of three overlapping plates stacks one gap apart and starts settled.
        StackLayout layout;
        layout.SetSpacing(0.5f);
        std::vector<PlateBox> column = Column();
        layout.Update(column.data(), 3, 1000);
        Check(Near(column[0].y + layout.Offset(0), 0.50f) && Near(column[1].y + layout.Offset(1), 0.50f + Gap)
            && Near(column[2].y + layout.Offset(2), 0.50f + 2 * Gap) && !layout.Moving(),
            "column stacks at 100% spacing and starts settled");

        // When the bottom plate moves away, the others glide down and settle within a second.
        column[0].y = 0.9f;
        uint32_t now = 1000;
        int frames = 0;
        do
        {
            now += 16;
            ++frames;
            layout.Update(column.data(), 3, now);
        } while (layout.Moving() && frames < 100);
        Check(frames < 60 && Near(layout.Offset(1), 0.0f) && Near(layout.Offset(2), Gap - 0.01f),
            "plates glide back and settle after " + std::to_string(frames) + " frames");

        // Halfway through a glide the plate is between where it was and where it goes.
        StackLayout glide;
        glide.SetSpacing(0.5f);
        std::vector<PlateBox> pair = { Column()[0], Column()[1] };
        glide.Update(pair.data(), 2, 1000);
        pair[0].y = 0.9f;
        glide.Update(pair.data(), 2, 1050);
        Check(glide.Moving() && Near(glide.Offset(1), (Gap - 0.01f) * 0.5f), "50 ms moves half the remaining distance");

        // Plates side by side don't stack.
        StackLayout apart;
        apart.SetSpacing(0.5f);
        std::vector<PlateBox> sideBySide = { { reinterpret_cast<void*>(1), 0.3f, 0.5f, 0.1f, 0.03f }, { reinterpret_cast<void*>(2), 0.5f, 0.5f, 0.1f, 0.03f } };
        apart.Update(sideBySide.data(), 2, 1000);
        Check(apart.Offset(0) == 0 && apart.Offset(1) == 0, "plates side by side keep their places");

        // The spacing setting scales the gap (70% -> 0.35 of the summed heights).
        StackLayout tight;
        tight.SetSpacing(70 / 200.0f);
        std::vector<PlateBox> two = { Column()[0], Column()[1] };
        tight.Update(two.data(), 2, 1000);
        Check(Near(two[1].y + tight.Offset(1), 0.50f + 0.06f * 0.35f), "70% spacing gives a 0.021 gap");

        // A crowd of 60 ends with no two overlapping plates.
        StackLayout crowd;
        crowd.SetSpacing(0.5f);
        std::vector<PlateBox> many;
        uint32_t seed = 5;
        auto next = [&seed]() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / 16777216.0f; };
        for (int i = 0; i < 60; ++i)
        {
            float x = 0.3f + 0.4f * next();
            float y = 0.3f + 0.3f * next();
            many.push_back({ reinterpret_cast<void*>(static_cast<intptr_t>(i + 1)), x, y, 0.1f, 0.03f });
        }
        crowd.Update(many.data(), 60, 5000);
        int overlaps = 0;
        for (int i = 0; i < 60; ++i)
        {
            for (int j = i + 1; j < 60; ++j)
            {
                bool columnOverlap = std::fabs(many[i].x - many[j].x) < 0.1f - 1e-6f;
                bool rowOverlap = std::fabs(many[i].y + crowd.Offset(i) - many[j].y - crowd.Offset(j)) < Gap - 1e-5f;
                overlaps += columnOverlap && rowOverlap ? 1 : 0;
            }
        }
        Check(crowd.Count() == 60 && overlaps == 0, "crowd of 60: " + std::to_string(overlaps) + " overlaps left");

        // More than 64 plates: only the first 64 are laid out.
        StackLayout full;
        std::vector<PlateBox> lots(70, PlateBox { nullptr, 0.5f, 0.5f, 0.1f, 0.03f });
        full.Update(lots.data(), 70, 1000);
        Check(full.Count() == MaxStackedPlates, "70 plates: 64 laid out");

        // An unknown plate gets no offset.
        Check(*layout.OffsetFor(reinterpret_cast<void*>(99)) == 0.0f, "unknown plate has no offset");

        // The layout wrapper runs the pass from the client's plates, then reaches the original with registers kept.
        s_plates = Column();
        s_now = 2000;
        g_stackLayout = StackLayout();
        g_stackLayout.SetSpacing(0.5f);
        g_stackClient = &s_mockStackClient;
        g_layoutNameplates = reinterpret_cast<void*>(&FakeOriginal);
        Check(CallWrapper(&StackThenLayout) && g_stackLayout.Count() == 3, "layout wrapper keeps registers and runs the pass");
        g_stackClient = &g_gameStackClient;

        // The y load adds the plate's offset, pushes 1 and keeps eax.
        uint32_t pushed = 0;
        bool eaxKept = false;
        float y = CallLoadStackedY(reinterpret_cast<void*>(3), 0.52f, pushed, eaxKept);
        Check(Near(y, 0.50f + 2 * Gap) && pushed == 1 && eaxKept, "y load: plate 3 at 0.52 -> " + std::to_string(y) + ", pushes 1");
        y = CallLoadStackedY(reinterpret_cast<void*>(42), 0.25f, pushed, eaxKept);
        Check(y == 0.25f && pushed == 1, "y load: an unknown plate keeps its y");

        // The flag fix clears the layout flag, and sets it again while plates are moving.
        uint32_t settled = CallFlagFix(7, false);
        Check(settled == 6, "flag fix, settled: 7 -> " + std::to_string(settled));
        uint32_t moving = CallFlagFix(6, true);
        Check(moving == 7, "flag fix, moving: 6 -> " + std::to_string(moving));
    }
}

void NameplateTests()
{
    SortTests();
    StackTests();
}
