#pragma once
#include <cstdint>

// Dirty values: the client writes 1 for anything that needs a fresh upload (a device reset, the cursor shown again);
// a plain cursor change writes CursorChangedOnly instead, unless 1 is already pending.
constexpr uint32_t CursorChangedOnly = 2;

// Whether the cursor update should upload, given the device the client passes. Skips (and clears the dirty flag) when
// only plain cursor changes happened and the image, hotspot, device and texture all match the last upload.
bool CursorUploadNeeded(uint8_t* device);
void ForgetCursorUpload();

extern "C" void CursorChanged();
extern "C" void CursorUploadCheck();

// The game's input getter, read from the patched calls; tests point it at a mock.
using InputGetFn = int(__cdecl*)(uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time);
extern void* g_inputGet;

// Stand-ins for the game's per-frame input loop: the first call of a pass, then the rest. Within a pass, mouse moves
// in a row collapse into the newest, and at most one is handed on when the queue runs dry; any other event first
// gets the pending move delivered ahead of it. A move held back is handed on in the next pass.
extern "C" int __cdecl FirstInputEvent(uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time);
extern "C" int __cdecl NextInputEvent(uint32_t* type, uint32_t* a, uint32_t* b, uint32_t* c, uint32_t* time);
void ForgetInputEvents();
