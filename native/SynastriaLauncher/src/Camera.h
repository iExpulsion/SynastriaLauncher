#pragma once
#include <cstdint>

// Camera Zoom: the zoom limit in yards, and what the game's Max Camera Distance factor (1 to 2) multiplies:
// half of it, so the in-game slider spans half the distance to all of it.
extern float g_cameraZoomMax;
extern float g_cameraZoomHalf;

// Field of View: the angle the game's camera is created with, in radians, kept equal to the cameraFov setting.
extern float g_cameraFovRadians;

// The angle in degrees as radians, when the text is a number within the allowed range.
bool ParseFovDegrees(const char* text, float& radians);

// Gives the world frame's camera, when there is one, a new angle.
void SetActiveCameraFov(float radians);

// Where the world frame pointer lives, and the client's CVar functions; tests point these at mocks.
extern uintptr_t g_worldFrameAddress;
using RegisterCvarFn = void*(__cdecl*)(const char* name, const char* help, uint32_t flags, const char* value,
    bool(__cdecl* changed)(void* cvar, const char* oldValue, const char* newValue, void* user), uint32_t category,
    bool arg7, void* user, bool arg9);
using SetCvarFn = bool(__thiscall*)(void* cvar, const char* value, bool runChanged, bool, bool, bool);
using ConsoleWriteFn = void(__cdecl*)(const char* format, int color, ...);
extern RegisterCvarFn g_registerCvar;
extern SetCvarFn g_setCvar;
extern ConsoleWriteFn g_consoleWrite;
extern void* g_registerCameraCvars;

// What a change to cameraFov runs: takes the new angle when it's in range, else reports the range and refuses it.
extern "C" bool __cdecl CameraFovChanged(void* cvar, const char* oldValue, const char* newValue, void* user);

// Stands in for the game UI's call that registers the camera settings: those first, then cameraFov, set to the
// launcher's angle the first time.
extern "C" void __cdecl CameraRegisterCvars();
void ForgetCameraFovStart();
