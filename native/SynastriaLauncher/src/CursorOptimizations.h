#pragma once
#include <cstdint>

// Dirty values: the client writes 1 for anything that needs a fresh upload (a device reset, the cursor shown again);
// a plain cursor change writes CursorChangedOnly instead, unless 1 is already pending.
constexpr uint32_t CursorChangedOnly = 2;

// Whether the cursor update should upload, given the device the client passes. Skips (and clears the dirty flag) when
// only plain cursor changes happened and the image, hotspot, device and texture all match the last upload.
bool CursorUploadNeeded(uint8_t* device);
void ForgetCursorUpload();

// Whether the device's hardware cursor is shown, so WM_SETCURSOR leaves the Windows cursor alone instead of hiding it
// for the device to show again. Otherwise the Windows cursor is hidden through g_hideWindowsCursor, as the game did.
bool HardwareCursorShown(const uint8_t* device);
extern void (*g_hideWindowsCursor)();

extern "C" void CursorChanged();
extern "C" void CursorUploadCheck();
extern "C" void WindowSetCursor();
