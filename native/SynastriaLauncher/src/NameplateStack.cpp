#include <cmath>
#include "Client.h"
#include "NameplateStack.h"
#include "Patch.h"

void* g_layoutNameplates = nullptr;
StackLayout g_stackLayout;
uint32_t g_stackMoving = 0;

namespace
{
    const float g_noOffset = 0.0f;
}

void StackLayout::Update(const PlateBox* plates, int count, uint32_t now)
{
    m_count = count < MaxStackedPlates ? count : MaxStackedPlates;
    int order[MaxStackedPlates];
    float target[MaxStackedPlates];
    for (int i = 0; i < m_count; ++i)
    {
        m_frame[i] = plates[i].frame;
        int j = i;
        while (j > 0 && plates[order[j - 1]].y > plates[i].y)
        {
            order[j] = order[j - 1];
            --j;
        }
        order[j] = i;
    }

    // Bottom to top, each plate rises above every plate already placed that it overlaps, repeating until it stays put.
    for (int position = 0; position < m_count; ++position)
    {
        const PlateBox& plate = plates[order[position]];
        float y = plate.y;
        bool moved = true;
        for (int pass = position; pass >= 0 && moved; --pass)
        {
            moved = false;
            for (int below = 0; below < position; ++below)
            {
                const PlateBox& other = plates[order[below]];
                float gap = (plate.height + other.height) * m_spacing;
                float placed = target[order[below]];
                bool sideBySide = std::fabs(plate.x - other.x) >= (plate.width + other.width) * 0.5f;
                if (!sideBySide && std::fabs(y - placed) < gap)
                {
                    y = placed + gap;
                    moved = true;
                }
            }
        }
        target[order[position]] = y;
    }

    float step = static_cast<float>(static_cast<int32_t>(now - m_lastTime)) * EaseRate;
    step = step > 1.0f ? 1.0f : step;
    m_lastTime = now;
    m_moving = false;
    for (int i = 0; i < m_count; ++i)
    {
        float offset = target[i] - plates[i].y;
        int previous = m_previousCount - 1;
        while (previous >= 0 && m_previousFrame[previous] != plates[i].frame)
        {
            --previous;
        }
        float remaining = previous >= 0 ? offset - m_previousOffset[previous] : 0.0f;
        if (previous >= 0 && std::fabs(remaining) >= SettleDistance)
        {
            offset = m_previousOffset[previous] + remaining * step;
            m_moving = true;
        }
        m_offset[i] = offset;
    }

    m_previousCount = m_count;
    for (int i = 0; i < m_count; ++i)
    {
        m_previousFrame[i] = m_frame[i];
        m_previousOffset[i] = m_offset[i];
    }
}

const float* StackLayout::OffsetFor(const void* frame) const
{
    for (int i = m_count - 1; i >= 0; --i)
    {
        if (m_frame[i] == frame)
        {
            return &m_offset[i];
        }
    }
    return &g_noOffset;
}

namespace
{
    using GetSizeFn = float(__thiscall*)(void* self);
    using GetTimeFn = uint32_t(__cdecl*)();

    float Size(uint8_t* plate, uint32_t slot)
    {
        void* layout = plate + Client::PlateLayout;
        auto* vtable = *static_cast<uintptr_t**>(layout);
        return reinterpret_cast<GetSizeFn>(vtable[slot / 4])(layout);
    }

    int GameCollect(PlateBox* out, int capacity)
    {
        int count = 0;
        uintptr_t plate = *reinterpret_cast<const uintptr_t*>(Client::NameplateListHead);
        while (plate != 0 && (plate & 1) == 0 && count < capacity)
        {
            auto* bytes = reinterpret_cast<uint8_t*>(plate);
            out[count].frame = bytes;
            out[count].x = *reinterpret_cast<const float*>(bytes + Client::PlateScreenX);
            out[count].y = *reinterpret_cast<const float*>(bytes + Client::PlateScreenY);
            out[count].width = Size(bytes, Client::VtableGetWidth);
            out[count].height = Size(bytes, Client::VtableGetHeight);
            ++count;
            plate = *reinterpret_cast<const uintptr_t*>(bytes + Client::PlateNext);
        }
        return count;
    }

    uint32_t GameNow()
    {
        return reinterpret_cast<GetTimeFn>(Client::GetTimeMs)();
    }

    void __cdecl RunStackPass()
    {
        PlateBox plates[MaxStackedPlates];
        int count = g_stackClient->collect(plates, MaxStackedPlates);
        g_stackLayout.Update(plates, count, g_stackClient->nowMs());
        g_stackMoving = g_stackLayout.Moving() ? 1 : 0;
    }
}

extern "C" const float* __cdecl StackedOffset(const void* frame)
{
    return g_stackLayout.OffsetFor(frame);
}

const StackClient g_gameStackClient { GameCollect, GameNow };
const StackClient* g_stackClient = &g_gameStackClient;

extern "C" __declspec(naked) void StackThenLayout()
{
    __asm
    {
        pushad
        pushfd
        call RunStackPass
        popfd
        popad
        jmp dword ptr [g_layoutNameplates]
    }
}

// The replaced code is "fld dword ptr [ebp-8]; push 1" with ebx holding the plate; ecx and edx are free there.
extern "C" __declspec(naked) void LoadStackedY()
{
    __asm
    {
        push eax
        push ebx
        call StackedOffset
        add esp, 4
        mov edx, eax
        pop eax
        pop ecx
        fld dword ptr [ebp - 8]
        fadd dword ptr [edx]
        push 1
        jmp ecx
    }
}

// The replaced code is "and dword ptr [esi+0B10h], 0FFFFFFFEh": bit 0 of the world frame's flags asks for another
// nameplate layout pass.
extern "C" __declspec(naked) void ClearLayoutFlagUnlessMoving()
{
    __asm
    {
        and dword ptr [esi + 0B10h], 0FFFFFFFEh
        cmp dword ptr [g_stackMoving], 0
        je done
        or dword ptr [esi + 0B10h], 1
    done:
        ret
    }
}

// The world frame's update calls its nameplate layout pass (push the world frame; call), then clears bit 0 of its
// flags. Inside the pass, each plate's y is loaded just before SetPoint, with the plate in ebx; eax holds a SetPoint
// argument there, so LoadStackedY keeps it.
Patch NameplateStackPatch()
{
    return Patch
    {
        "NameplateStack",
        {
            Code("LayoutPass", Hex("6A 00 E8 B4 C7 11 00 56 E8 AE C7 22 00 83 C4 08"),
                CallTo(8, reinterpret_cast<const void*>(&StackThenLayout), &g_layoutNameplates)),
            Code("PlateY", Hex("74 05 8D 46 20 EB 02 33 C0") + Hex("D9 45 F8 6A 01") + Hex("83 EC 08"),
                CallTo(9, reinterpret_cast<const void*>(&LoadStackedY))),
            Code("LayoutFlag", Hex("83 C4 08") + Hex("83 A6 10 0B 00 00 FE") + Hex("8B 3D 4C 75 CD 00"),
                CallTo(3, reinterpret_cast<const void*>(&ClearLayoutFlagUnlessMoving), nullptr, 2)),
        },
        [](const PatchConfig& config)
        {
            g_layoutNameplates = nullptr;
            g_stackLayout.SetSpacing(static_cast<float>(config.stackSpacing / 200.0));
        },
    };
}
