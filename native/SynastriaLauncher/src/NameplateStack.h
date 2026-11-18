#pragma once
#include <cstdint>

// Pushes overlapping nameplates apart. Before each nameplate layout pass of the world frame, the plates' raw screen
// positions and sizes are read and sorted bottom to top, and each plate is raised above any plate below it that it
// overlaps, by both plates' heights times the spacing setting. Each plate's offset then eases toward that target
// with the client's millisecond clock, so plates glide instead of jumping. The layout pass adds a plate's offset to
// its y just before SetPoint; the raw positions are never changed, so offsets never feed back. While any plate is
// still moving, the world frame's "nameplates need layout" flag is set again so the next frame runs another pass.

constexpr int MaxStackedPlates = 64;

struct PlateBox
{
    void* frame;
    float x;
    float y;
    float width;
    float height;
};

class StackLayout
{
public:
    static constexpr float EaseRate = 1.0f / 100.0f;   // fraction of the remaining distance per millisecond
    static constexpr float SettleDistance = 0.0005f;

    // spacing is the gap between stacked plates as a share of their summed heights (percent / 200).
    void SetSpacing(float spacing) { m_spacing = spacing; }

    // Lays out this pass's plates at time now. Plates new since the last pass start at their target.
    void Update(const PlateBox* plates, int count, uint32_t now);

    // The y offset for a plate in the last pass, or 0.
    const float* OffsetFor(const void* frame) const;

    bool Moving() const { return m_moving; }
    int Count() const { return m_count; }
    float Offset(int index) const { return m_offset[index]; }

private:
    float m_spacing = 0.5f;
    int m_count = 0;
    void* m_frame[MaxStackedPlates] = {};
    float m_offset[MaxStackedPlates] = {};
    int m_previousCount = 0;
    void* m_previousFrame[MaxStackedPlates] = {};
    float m_previousOffset[MaxStackedPlates] = {};
    uint32_t m_lastTime = 0;
    bool m_moving = false;
};

// What the stacking pass needs from the client; tests supply their own.
struct StackClient
{
    int (*collect)(PlateBox* out, int capacity);
    uint32_t (*nowMs)();
};

extern const StackClient g_gameStackClient;
extern const StackClient* g_stackClient;
extern StackLayout g_stackLayout;
extern uint32_t g_stackMoving;
extern void* g_layoutNameplates;

extern "C" const float* __cdecl StackedOffset(const void* frame);

// Called in place of the world frame's nameplate layout pass: runs the stacking pass, then the original.
extern "C" void StackThenLayout();

// Replaces the layout pass's load of a plate's y (ebx is the plate) and the push after it: loads y plus the plate's
// offset and pushes 1.
extern "C" void LoadStackedY();

// Replaces the clearing of the world frame's layout flag (esi is the world frame): clears it, then sets it again
// while plates are still moving.
extern "C" void ClearLayoutFlagUnlessMoving();
