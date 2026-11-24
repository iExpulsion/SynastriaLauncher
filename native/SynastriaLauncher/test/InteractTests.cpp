// Unit tests for the Interact Key: which candidate is picked (range, reach, facing, the any-direction circle, the
// nearest of several), the binding and /interact text, and the InteractUnit wrapper's flow (pass-through, "nearest", the
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
        MockType, MockToString, MockInteractUnit, MockTainted, MockBlocked, MockNearest,
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
        return InteractCandidate { guid, { x, y, z }, reach, false, 0 };
    }

    InteractCandidate Loot(uint64_t guid, float x, float y, uint32_t opened = 0)
    {
        return InteractCandidate { guid, { x, y, 0 }, 0, true, opened };
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

    // Loot opened lately gives way to the nearest loot not opened lately.
    InteractCandidate reopened[] = { Loot(1, 2, 0, 5), Loot(2, 4, 0), Loot(3, 3, 0) };
    Check(PickInteraction(Facing, reopened, 3) == 3, "loot cycle: the nearest unopened loot replaces loot just opened");

    // With everything opened lately, the one opened longest ago comes round again.
    InteractCandidate allOpened[] = { Loot(1, 2, 0, 5), Loot(2, 4, 0, 3), Loot(3, 3, 0, 4) };
    Check(PickInteraction(Facing, allOpened, 3) == 2, "loot cycle: all opened, the one opened longest ago");

    // Only loot moves on; anything else nearer still wins, and unopened loot nearest is taken as usual.
    InteractCandidate npcFirst[] = { At(1, 1, 0), Loot(2, 2, 0, 5), Loot(3, 3, 0) };
    Check(PickInteraction(Facing, npcFirst, 3) == 1, "loot cycle: a nearer NPC is still picked");
    InteractCandidate herbAfter[] = { Loot(1, 2, 0, 5), At(2, 3, 0) };
    Check(PickInteraction(Facing, herbAfter, 2) == 1, "loot cycle: just-opened loot with no other loot is reopened, not swapped for a herb");
    InteractCandidate freshFirst[] = { Loot(1, 2, 0), Loot(2, 3, 0, 5) };
    Check(PickInteraction(Facing, freshFirst, 2) == 1, "loot cycle: unopened loot nearest is picked");

    // Loot out of reach or behind isn't cycled to.
    InteractCandidate cycleReach[] = { Loot(1, 2, 0, 5), Loot(2, 9, 0), Loot(3, -4, 0) };
    Check(PickInteraction(Facing, cycleReach, 3) == 1, "loot cycle: unreachable loot is ignored");

    // The skill a corpse needs, as its tooltip works it out.
    Check(GatherSkillNeeded(1) == 1 && GatherSkillNeeded(10) == 1, "gather skill: up to level 10 needs 1");
    Check(GatherSkillNeeded(11) == 10 && GatherSkillNeeded(19) == 90, "gather skill: levels 11-19 need 10 per level over 10");
    Check(GatherSkillNeeded(20) == 100 && GatherSkillNeeded(80) == 400, "gather skill: level 20 and up need 5 per level");

    // Recent loot: remembered with an increasing order, forgotten after 30 seconds.
    RecentLoot recent;
    recent.Opened(10, 1000);
    recent.Opened(11, 2000);
    Check(recent.Recency(10, 3000) != 0 && recent.Recency(11, 3000) > recent.Recency(10, 3000), "recent loot: later openings rank higher");
    Check(recent.Recency(12, 3000) == 0, "recent loot: loot never opened has no rank");
    Check(recent.Recency(10, 31000) == 0 && recent.Recency(11, 31000) != 0, "recent loot: forgotten after 30 seconds");
    recent.Opened(10, 4000);
    Check(recent.Recency(10, 5000) > recent.Recency(11, 5000), "recent loot: reopening moves it to the newest");

    // A full list forgets the oldest opening.
    RecentLoot full;
    for (uint64_t guid = 1; guid <= 17; ++guid)
    {
        full.Opened(guid, 100);
    }
    Check(full.Recency(1, 200) == 0 && full.Recency(2, 200) != 0 && full.Recency(17, 200) != 0, "recent loot: the 17th opening replaces the oldest");

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

    // "mouseover" is the client's own, as in the stock Interact With Mouseover binding.
    World hovered;
    hovered.arg = "mouseover";
    Check(Run(hovered) == 1 && s.originals == 1 && s.searches == 0, "InteractUnit(\"mouseover\"): passed through");

    // The key binding: one Binding under its own header, running InteractUnit("nearest").
    std::string xml = InteractBindingsXml;
    Check(xml.find("<Binding name=\"SYNASTRIALAUNCHER_INTERACT\" header=\"SYNASTRIALAUNCHER\">InteractUnit(\"nearest\")</Binding>")
        != std::string::npos && xml.rfind("<Bindings>", 0) == 0, "binding: Interact With Nearest under its own header");

    // The setup names the header and binding, and /interact parses conditions before interacting.
    std::string setup = InteractSetupScript;
    Check(setup.find("BINDING_HEADER_SYNASTRIALAUNCHER = \"Synastria Launcher\"") != std::string::npos
        && setup.find("BINDING_NAME_SYNASTRIALAUNCHER_INTERACT = \"Interact With Nearest\"") != std::string::npos
        && setup.find("SLASH_SYNASTRIALAUNCHER_INTERACT1 = \"/interact\"") != std::string::npos
        && setup.find("SecureCmdOptionParse") < setup.find("InteractUnit("), "setup: names, /interact with conditions");

    // Other units and non-string arguments go to the client's function.
    World target;
    target.arg = "target";
    Check(Run(target) == 1 && s.originals == 1 && s.searches == 0, "InteractUnit(\"target\"): passed through");
    World number;
    number.argType = 3;
    Check(Run(number) == 1 && s.originals == 1 && s.searches == 0, "InteractUnit(5): passed through");
}
