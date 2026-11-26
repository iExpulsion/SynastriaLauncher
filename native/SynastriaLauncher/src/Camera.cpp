// Camera Zoom and Field of View.
//
// The camera's farthest zoom is cameraDistanceMax (15 yards) times cameraDistanceMaxFactor (1 to 2 on the game's
// slider), capped at a 50-yard constant only the camera code reads. Zoom raises the cap to the launcher's distance and
// multiplies the factor by half of it instead of cameraDistanceMax.
//
// The world camera is created with a fixed 90-degree angle, which its projection reads every frame. Field of View
// creates it with the launcher's angle instead, and adds a cameraFov setting (degrees) that changes the angle live.
#include <cstdio>
#include <cstdlib>
#include "Camera.h"
#include "Client.h"
#include "Patch.h"
#include "Settings.h"

float g_cameraZoomMax = static_cast<float>(DefaultCameraZoom);
float g_cameraZoomHalf = static_cast<float>(DefaultCameraZoom) / 2.0f;
float g_cameraFovRadians = static_cast<float>(DefaultCameraFov) * Client::RadiansPerDegree;

uintptr_t g_worldFrameAddress = Client::WorldFrame;
RegisterCvarFn g_registerCvar = reinterpret_cast<RegisterCvarFn>(Client::RegisterCvar);
SetCvarFn g_setCvar = reinterpret_cast<SetCvarFn>(Client::SetCvar);
ConsoleWriteFn g_consoleWrite = reinterpret_cast<ConsoleWriteFn>(Client::ConsoleWrite);
void* g_registerCameraCvars = nullptr;

namespace
{
    bool g_fovStartSet = false;
    char g_launcherFovText[16] = "90";
}

bool ParseFovDegrees(const char* text, float& radians)
{
    char* end = nullptr;
    double degrees = text != nullptr ? strtod(text, &end) : 0.0;
    bool ok = text != nullptr && end != text && degrees >= MinCameraFov && degrees <= MaxCameraFov;
    radians = ok ? static_cast<float>(degrees) * Client::RadiansPerDegree : radians;
    return ok;
}

void SetActiveCameraFov(float radians)
{
    uint8_t* worldFrame = *reinterpret_cast<uint8_t**>(g_worldFrameAddress);
    uint8_t* camera = worldFrame != nullptr ? *reinterpret_cast<uint8_t**>(worldFrame + Client::WorldActiveCamera) : nullptr;
    if (camera != nullptr)
    {
        *reinterpret_cast<float*>(camera + Client::CameraFov) = radians;
    }
}

extern "C" bool __cdecl CameraFovChanged(void*, const char*, const char* newValue, void*)
{
    bool ok = ParseFovDegrees(newValue, g_cameraFovRadians);
    if (ok)
    {
        SetActiveCameraFov(g_cameraFovRadians);
    }
    else
    {
        g_consoleWrite(reinterpret_cast<const char*>(Client::OutOfRangeFormat), 0,
            static_cast<double>(MinCameraFov), static_cast<double>(MaxCameraFov));
    }
    return ok;
}

extern "C" void __cdecl CameraRegisterCvars()
{
    reinterpret_cast<void(__cdecl*)()>(g_registerCameraCvars)();
    void* cvar = g_registerCvar("cameraFov", nullptr, Client::CvarFlagsCamera, g_launcherFovText, &CameraFovChanged,
        Client::CvarCategoryCamera, false, nullptr, false);
    if (cvar != nullptr && !g_fovStartSet)
    {
        g_fovStartSet = true;
        g_setCvar(cvar, g_launcherFovText, true, false, false, true);
    }
}

void ForgetCameraFovStart()
{
    g_fovStartSet = false;
}

Patch CameraZoomPatch()
{
    return Patch
    {
        "CameraZoom",
        {
            // The 50-yard cap, between 0.5 and 0.
            Code("DistanceCap", Hex("00 00 00 3F") + F32(50.0f) + Hex("00 00 00 00"),
                WriteBytes(4, [] { return F32(g_cameraZoomMax); })),
            // fld [factor + value]; mov ecx, [cameraDistanceMax]; fmul [ecx + value] becomes fmul [g_cameraZoomHalf].
            Code("DistanceFactor", Hex("A1 88 49 C2 00 D9 40 2C 8B 0D 8C 49 C2 00 D8 49 2C"),
                WriteBytes(8, [] { return Hex("D8 0D") + U32(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(&g_cameraZoomHalf))) + Hex("90 90 90"); })),
        },
        [](const PatchConfig& config)
        {
            g_cameraZoomMax = static_cast<float>(config.cameraZoom);
            g_cameraZoomHalf = g_cameraZoomMax / 2.0f;
        },
    };
}

Patch CameraFovPatch()
{
    return Patch
    {
        "CameraFov",
        {
            // The world camera's constructor call: fld [90 degrees] becomes fld [g_cameraFovRadians].
            Code("CreateCamera", Hex("D9 05 88 8D 9E 00 56 57 83 EC 0C D9 5C 24 08 8B F1"),
                WriteBytes(2, [] { return U32(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(&g_cameraFovRadians))); })),
            Code("RegisterCameraSettings", Hex("83 C4 48 E8 BE EE 0D 00 6A 00 6A 00"),
                CallTo(3, reinterpret_cast<const void*>(&CameraRegisterCvars), &g_registerCameraCvars)),
        },
        [](const PatchConfig& config)
        {
            g_registerCameraCvars = nullptr;
            snprintf(g_launcherFovText, sizeof(g_launcherFovText), "%d", config.cameraFov);
            g_cameraFovRadians = static_cast<float>(config.cameraFov) * Client::RadiansPerDegree;
        },
    };
}
