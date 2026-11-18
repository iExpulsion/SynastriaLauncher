// Unit tests for the ground target macro conditionals: token parsing, where each one resolves, and the
// CastSpellByName wrapper's flow (pass-through, cast without the token, place at the point, or cancel) against a mock
// client.
#include <string>
#include <vector>
#include "../src/GroundTargets.h"
#include "NativeTests.h"

namespace
{
    struct World
    {
        int top = 2;
        int argType = 4;
        std::string arg = "cursor";
        bool groundSpell = true;
        bool stayAfterClick = false;
        bool overWorld = true;
        uint32_t hover = 1;
        float hoverPoint[3] = { 1, 2, 3 };
        bool hoverObject = true;
        float objectPoint[3] = { 4, 5, 6 };
        float playerPoint[3] = { 7, 8, 9 };
        bool hasTarget = true;
        float targetPoint[3] = { 10, 11, 12 };
        float freshPoint[3] = { 13, 14, 15 };
        bool stale = false;

        bool awaiting = false;
        int refreshes = 0;
        int castTop = -1;
        int casts = 0;
        int stops = 0;
        std::vector<float> placed;
    };

    World s;

    int MockType(void*, int index) { return index <= s.top ? (index == 2 ? s.argType : 4) : -1; }
    const char* MockToString(void*, int index) { return index == 2 ? s.arg.c_str() : "Flamestrike"; }
    int MockGetTop(void*) { return s.top; }
    void MockSetTop(void*, int top) { s.top = top; }

    int MockCast(void*)
    {
        s.castTop = s.top;
        ++s.casts;
        s.awaiting = s.groundSpell;
        return 0;
    }

    void MockStop(void*)
    {
        ++s.stops;
        s.awaiting = false;
        ++s.top;
    }

    bool MockAwaiting() { return s.awaiting; }

    bool MockPlace(const float* point)
    {
        s.placed.assign(point, point + 3);
        s.awaiting = s.stayAfterClick;
        return true;
    }

    bool MockOverWorld() { return s.overWorld; }
    uint32_t MockHoverType() { return s.hover; }
    void MockHoverPoint(float* out) { std::copy(s.hoverPoint, s.hoverPoint + 3, out); }

    bool MockHoverObject(float* out)
    {
        std::copy(s.objectPoint, s.objectPoint + 3, out);
        return s.hoverObject;
    }

    bool MockPlayer(float* out)
    {
        std::copy(s.playerPoint, s.playerPoint + 3, out);
        return true;
    }

    bool MockTarget(float* out)
    {
        std::copy(s.targetPoint, s.targetPoint + 3, out);
        return s.hasTarget;
    }

    // The client's hover pick: a stale world gets the point under the mouse now.
    void MockRefresh()
    {
        ++s.refreshes;
        if (s.stale)
        {
            std::copy(s.freshPoint, s.freshPoint + 3, s.hoverPoint);
        }
    }

    const GroundClient s_mock
    {
        MockType, MockToString, MockGetTop, MockSetTop, MockCast, MockStop, MockAwaiting, MockPlace, MockOverWorld,
        MockHoverType, MockHoverPoint, MockHoverObject, MockPlayer, MockTarget, MockRefresh,
    };

    void Run(const World& world)
    {
        s = world;
        CastSpellByNameWithGround(nullptr, s_mock);
    }

    bool PlacedAt(float x, float y, float z)
    {
        return s.placed == std::vector<float> { x, y, z };
    }

    // Cast once with the token removed, placed at the point, nothing cancelled, the stack left as the cast left it.
    bool PlacedCleanly(float x, float y, float z)
    {
        return s.casts == 1 && s.castTop == 1 && PlacedAt(x, y, z) && s.stops == 0 && s.top == 1;
    }

    // Cast once with the token removed, then targeting cancelled with nothing placed and the stack restored.
    bool Cancelled()
    {
        return s.casts == 1 && s.castTop == 1 && s.placed.empty() && s.stops == 1 && s.top == 1;
    }
}

void GroundTests()
{
    // The three tokens, in any case; anything else is a normal unit.
    Check(ParseGroundTarget("cursor") == GroundTarget::Cursor && ParseGroundTarget("CURSOR") == GroundTarget::Cursor,
        "ground token: cursor, any case");
    Check(ParseGroundTarget("playerlocation") == GroundTarget::PlayerLocation && ParseGroundTarget("PlayerLocation") == GroundTarget::PlayerLocation,
        "ground token: playerlocation");
    Check(ParseGroundTarget("targetlocation") == GroundTarget::TargetLocation, "ground token: targetlocation");

    // The short forms cur, ploc and tloc, in any case.
    Check(ParseGroundTarget("cur") == GroundTarget::Cursor && ParseGroundTarget("Ploc") == GroundTarget::PlayerLocation
        && ParseGroundTarget("TLOC") == GroundTarget::TargetLocation, "ground token: cur, ploc, tloc");
    Check(ParseGroundTarget("target") == GroundTarget::None && ParseGroundTarget("player") == GroundTarget::None
        && ParseGroundTarget("") == GroundTarget::None && ParseGroundTarget(nullptr) == GroundTarget::None
        && ParseGroundTarget("cursor2") == GroundTarget::None,
        "ground token: target, player, empty and others are not ground tokens");

    // Normal macros are untouched: the cast gets its target argument and nothing else happens.
    World normal;
    normal.arg = "target";
    Run(normal);
    Check(s.casts == 1 && s.castTop == 2 && s.placed.empty() && s.stops == 0, "[@target]: passed through with its target");
    World noTarget;
    noTarget.top = 1;
    Run(noTarget);
    Check(s.casts == 1 && s.castTop == 1 && s.placed.empty() && s.stops == 0, "no target argument: passed through");
    World number;
    number.argType = 3;
    Run(number);
    Check(s.casts == 1 && s.castTop == 2 && s.placed.empty(), "a non-string target argument: passed through");

    // [@cursor]: the ground under the mouse, or the ground position of an object under it.
    Run(World());
    Check(PlacedCleanly(1, 2, 3), "[@cursor] over the ground: placed at the hover point");
    World hover3;
    hover3.hover = 3;
    Run(hover3);
    Check(PlacedCleanly(1, 2, 3), "[@cursor] hover type 3: placed at the hover point");
    World overObject;
    overObject.hover = 2;
    Run(overObject);
    Check(PlacedCleanly(4, 5, 6), "[@cursor] over a unit: placed at its position");
    World goneObject;
    goneObject.hover = 2;
    goneObject.hoverObject = false;
    Run(goneObject);
    Check(Cancelled(), "[@cursor] over an object that can't be found: cancelled");
    World sky;
    sky.hover = 0;
    Run(sky);
    Check(Cancelled(), "[@cursor] over nothing (sky): cancelled");
    World overUi;
    overUi.overWorld = false;
    Run(overUi);
    Check(Cancelled(), "[@cursor] with the mouse over the interface: cancelled");
    World upper;
    upper.arg = "Cursor";
    Run(upper);
    Check(PlacedCleanly(1, 2, 3), "[@Cursor] works in any case");

    // [@cursor] picks again before reading the point: the client only does it on mouse clicks, so a keybind press
    // would otherwise use the last click's spot. [@playerlocation] doesn't pick.
    World stale;
    stale.stale = true;
    Run(stale);
    Check(PlacedCleanly(13, 14, 15) && s.refreshes == 1, "[@cursor] with a stale hover point: picked again, placed under the mouse");
    World staleOverUi;
    staleOverUi.stale = true;
    staleOverUi.overWorld = false;
    Run(staleOverUi);
    Check(Cancelled() && s.refreshes == 0, "[@cursor] over the interface: no pick, cancelled");
    World selfNoPick;
    selfNoPick.arg = "ploc";
    Run(selfNoPick);
    Check(PlacedCleanly(7, 8, 9) && s.refreshes == 0, "[@ploc]: placed at the player without a pick");

    // [@playerlocation] and [@targetlocation].
    World self;
    self.arg = "playerlocation";
    Run(self);
    Check(PlacedCleanly(7, 8, 9), "[@playerlocation]: placed at the player");
    World target;
    target.arg = "targetlocation";
    Run(target);
    Check(PlacedCleanly(10, 11, 12), "[@targetlocation]: placed at the target");
    World noUnit;
    noUnit.arg = "targetlocation";
    noUnit.hasTarget = false;
    Run(noUnit);
    Check(Cancelled(), "[@targetlocation] with no target: cancelled");

    // A spell that doesn't need a ground point is cast as usual and left alone.
    World notGround;
    notGround.groundSpell = false;
    Run(notGround);
    Check(s.casts == 1 && s.castTop == 1 && s.placed.empty() && s.stops == 0, "[@cursor] on a spell without a ground target: cast, nothing else");

    // If the client keeps waiting for a point after the click (it refused it), targeting is cancelled.
    World refused;
    refused.stayAfterClick = true;
    Run(refused);
    Check(PlacedAt(1, 2, 3) && s.stops == 1 && s.top == 1, "a point the client refuses: cancelled after the click");
}
