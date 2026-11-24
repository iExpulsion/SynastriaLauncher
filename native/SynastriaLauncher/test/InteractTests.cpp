// Unit tests for the Interact Key: which candidate is picked (range, reach, facing, the any-direction circle, the
// nearest of several), and the InteractUnit wrapper's flow (pass-through, "nearest", the mouseover fallback, the
// tainted case) against a mock client.
#include <string>
#include "../src/InteractKey.h"
#include "NativeTests.h"

namespace
{
    struct World
    {
        int argType = 4;
        std::string arg = "nearest";
        bool tainted = false;
        uint64_t mouseover = 0;
        bool mouseoverExists = false;
        uint64_t nearest = 42;
        bool interactWorks = true;

        int originals = 0;
        int blocks = 0;
        int searches = 0;
        uint64_t interacted = 0;
        int pushes = 0;
        bool pushed = false;
    };

    World s;

    int MockType(void*, int index) { return index == 1 ? s.argType : -1; }
    const char* MockToString(void*, int) { return s.arg.c_str(); }

    int MockInteractUnit(void*)
    {
        ++s.originals;
        return 1;
    }

    bool MockTainted() { return s.tainted; }
    void MockBlocked() { ++s.blocks; }
    uint64_t MockMouseover() { return s.mouseover; }
    bool MockExists(uint64_t guid) { return guid != 0 && guid == s.mouseover && s.mouseoverExists; }

    uint64_t MockNearest()
    {
        ++s.searches;
        return s.nearest;
    }

    bool MockInteract(uint64_t guid)
    {
        s.interacted = guid;
        return s.interactWorks;
    }

    void MockPush(void*, bool interacted)
    {
        ++s.pushes;
        s.pushed = interacted;
    }

    const InteractClient s_mock
    {
        MockType, MockToString, MockInteractUnit, MockTainted, MockBlocked, MockMouseover, MockExists, MockNearest,
        MockInteract, MockPush,
    };

    int Run(const World& world)
    {
        s = world;
        return InteractUnitWithNearest(nullptr, s_mock);
    }

    // A player at the origin facing +x (facing 0).
    const InteractViewer Facing { { 0, 0, 0 }, 0 };

    InteractCandidate At(uint64_t guid, float x, float y, float z = 0, float reach = 0)
    {
        return InteractCandidate { guid, { x, y, z }, reach };
    }
}

void InteractTests()
{
    // In front and in range: picked.
    InteractCandidate ahead[] = { At(1, 4, 0) };
    Check(PickInteraction(Facing, ahead, 1) == 1, "interact: an object 4 yards ahead is picked");

    // Out of range, unless the unit's reach covers it.
    InteractCandidate far[] = { At(1, 7, 0) };
    Check(PickInteraction(Facing, far, 1) == 0, "interact: an object 7 yards ahead is out of range");
    InteractCandidate bigUnit[] = { At(1, 7, 0, 0, 1.5f) };
    Check(PickInteraction(Facing, bigUnit, 1) == 1, "interact: a unit's combat reach extends the range");

    // Height counts toward the distance.
    InteractCandidate above[] = { At(1, 3, 0, 6) };
    Check(PickInteraction(Facing, above, 1) == 0, "interact: an object 6 yards up and 3 ahead is out of range");

    // Behind the player: skipped, unless it's right beside them.
    InteractCandidate behind[] = { At(1, -4, 0) };
    Check(PickInteraction(Facing, behind, 1) == 0, "interact: an object 4 yards behind is skipped");
    InteractCandidate underfoot[] = { At(1, -2, 0) };
    Check(PickInteraction(Facing, underfoot, 1) == 1, "interact: an object 2 yards behind counts as beside the player");

    // To the side counts as in front up to 90 degrees either way.
    InteractCandidate side[] = { At(1, 0.1f, 4) };
    Check(PickInteraction(Facing, side, 1) == 1, "interact: an object just ahead of square to the left is picked");

    // Facing is honored, wrapping around past pi.
    InteractViewer west { { 0, 0, 0 }, 3.1f };
    InteractCandidate westward[] = { At(1, -4, 0.2f), At(2, 4, 0) };
    Check(PickInteraction(west, westward, 2) == 1, "interact: facing about pi picks the object at -x, not the one at +x");

    // The nearest of several qualifying candidates wins.
    InteractCandidate several[] = { At(1, 5, 0), At(2, 3, 1), At(3, -1, 0), At(4, 9, 0) };
    Check(PickInteraction(Facing, several, 4) == 3, "interact: the nearest qualifying candidate wins");
    InteractCandidate none[] = { At(1, -5, 0), At(2, 20, 0) };
    Check(PickInteraction(Facing, none, 2) == 0 && PickInteraction(Facing, nullptr, 0) == 0, "interact: nothing qualifying picks nothing");

    // "nearest": interacts with what the search found and returns 1.
    Check(Run(World()) == 1 && s.interacted == 42 && s.pushes == 1 && s.pushed && s.originals == 0 && s.blocks == 0,
        "InteractUnit(\"nearest\"): interacts with the nearest and returns true");
    World caps;
    caps.arg = "NEAREST";
    Check(Run(caps) == 1 && s.interacted == 42, "InteractUnit(\"NEAREST\"): any case");

    // Nothing nearby: returns nil so the binding's fallback to the target runs.
    World empty;
    empty.nearest = 0;
    Check(Run(empty) == 1 && s.interacted == 0 && s.pushes == 1 && !s.pushed, "InteractUnit(\"nearest\") with nothing nearby: nil");
    World refused;
    refused.interactWorks = false;
    Check(Run(refused) == 1 && s.interacted == 42 && !s.pushed, "InteractUnit(\"nearest\") the client refuses: nil");

    // Tainted code gets the client's blocked-action report and nothing else.
    World tainted;
    tainted.tainted = true;
    Check(Run(tainted) == 0 && s.blocks == 1 && s.searches == 0 && s.interacted == 0 && s.pushes == 0 && s.originals == 0,
        "InteractUnit(\"nearest\") from tainted code: blocked");

    // "mouseover" with a unit under the mouse: the client's own function.
    World hovered;
    hovered.arg = "mouseover";
    hovered.mouseover = 7;
    hovered.mouseoverExists = true;
    Check(Run(hovered) == 1 && s.originals == 1 && s.searches == 0, "InteractUnit(\"mouseover\") over a unit: passed through");

    // "mouseover" with nothing under the mouse, or a unit that's gone: the nearest instead.
    World nothingHovered;
    nothingHovered.arg = "mouseover";
    Check(Run(nothingHovered) == 1 && s.originals == 0 && s.interacted == 42, "InteractUnit(\"mouseover\") over nothing: the nearest");
    World gone;
    gone.arg = "MouseOver";
    gone.mouseover = 7;
    Check(Run(gone) == 1 && s.originals == 0 && s.interacted == 42, "InteractUnit(\"MouseOver\") over a unit that's gone: the nearest");

    // Other units and non-string arguments go to the client's function.
    World target;
    target.arg = "target";
    Check(Run(target) == 1 && s.originals == 1 && s.searches == 0, "InteractUnit(\"target\"): passed through");
    World number;
    number.argType = 3;
    Check(Run(number) == 1 && s.originals == 1 && s.searches == 0, "InteractUnit(5): passed through");
}
