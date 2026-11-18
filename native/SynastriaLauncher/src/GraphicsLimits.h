#pragma once

// The video options sliders ask the client for each setting's maximum, and it knows only farclip's (1277); the
// others fall back to the sliders' own limits. The lookup is pointed at this table instead, with raised limits.
// Returns null for a setting it doesn't list, which takes the client's original "no maximum" path.
extern "C" const float* __cdecl SliderMaximum(const char* cvarName);
