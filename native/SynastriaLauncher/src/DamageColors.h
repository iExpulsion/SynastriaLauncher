#pragma once
#include <cstdint>
#include <vector>
#include "Engine.h"

// Tiers highest first, as ARGB colors; a damage amount takes the color of the first tier it reaches.
void SetDamageTiers(const std::vector<DamageTier>& tiers);

// The color for this amount, or null to keep the color the game picked.
extern "C" const uint32_t* __cdecl PickDamageColor(int amount);

extern void* g_createDamageText;

// Entered in place of the client's damage text call: the amount is read from the caller's frame (its first or
// second argument), then the color argument is swapped before going on to the client's function.
extern "C" void DamageTextFromSecondArg();
extern "C" void DamageTextFromFirstArg();
