#pragma once
#include <cstdint>
#include <functional>
#include <string>
#include <vector>
#include "Engine.h"

using Bytes = std::vector<uint8_t>;

Bytes Hex(const char* hex);
Bytes U32(uint32_t value);
Bytes F32(float value);
Bytes operator+(Bytes left, const Bytes& right);

// What a site writes: bytes at an offset from where its pattern matched.
struct Edit
{
    size_t offset = 0;
    Bytes bytes;
};

// Works out a site's edit from where it matched; returns why it can't, or an empty string.
using Builder = std::function<std::string(uint8_t* at, Edit& edit)>;

// One place a patch changes. The pattern must occur exactly once in the game's code. A byte swap writes its
// replacement over the match, and a game that already holds the replacement counts as patched. Other sites build
// their edit from the match, usually a jump or call into this DLL.
struct Site
{
    const char* name;
    Bytes find;
    Bytes swap;
    Builder build;
};

Site Swap(const char* name, Bytes find, Bytes replace);
Site Code(const char* name, Bytes find, Builder build);

struct Patch
{
    const char* id;
    std::vector<Site> sites;
    std::function<void(const PatchConfig&)> configure;
};

// Every patch, in the order they're applied.
const std::vector<Patch>& Patches();

// Builders for the common edits. Each takes the offset in the pattern where the new instruction goes. When
// `original` is set, it receives the address the pattern's own call, jump or push at that offset pointed to; a
// second site disagreeing with the first is refused.
Builder CallTo(size_t at, const void* target, void** original = nullptr, size_t nops = 0);
Builder JumpTo(size_t at, const void* target, size_t nops = 0);
Builder PushAddress(size_t at, const void* target, void** original);
Builder WriteBytes(size_t offset, std::function<Bytes()> bytes);

// Patch definitions, one per file.
Patch InterfaceEditPatch();
Patch NoAutoAfkPatch();
Patch NoMailDelayPatch();
Patch GraphicsLimitsPatch();
Patch NoZoomFadePatch();
Patch HdPortraitsPatch();
Patch NameplateDistancePatch();
Patch NameplateSortPatch();
Patch NameplateStackPatch();
Patch ShortNumbersPatch();
Patch DamageColorsPatch();
Patch GroundTargetsPatch();
Patch CursorOptimizationsPatch();
Patch InteractKeyPatch();
Patch CameraZoomPatch();
Patch CameraFovPatch();
