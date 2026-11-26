// Unit tests for Camera Zoom and Field of View: the zoom settings it writes, reading cameraFov's degrees, the
// active camera taking a new angle, out-of-range values refused with the range printed, and cameraFov registered after
// the stock camera settings and set to the launcher's angle only the first time.
#include <cmath>
#include <cstring>
#include <string>
#include <vector>
#include "../src/Camera.h"
#include "../src/Client.h"
#include "../src/Engine.h"
#include "../src/Patch.h"
#include "../src/Settings.h"
#include "NativeTests.h"

namespace
{
    std::vector<uint8_t> g_camera(0x100);
    std::vector<uint8_t> g_worldFrame(0x8000);
    uint8_t* g_worldFramePointer = nullptr;
    int g_stockRegistered = 0;
    int g_consoleLines = 0;
    std::vector<std::string> g_registered;
    std::vector<std::string> g_setValues;
    uint8_t g_fakeCvar = 0;

    float CameraAngle()
    {
        float value = 0;
        memcpy(&value, g_camera.data() + Client::CameraFov, 4);
        return value;
    }

    bool Near(float a, float b)
    {
        return std::fabs(a - b) < 0.0001f;
    }

    void __cdecl StockRegister()
    {
        ++g_stockRegistered;
    }

    void* __cdecl MockRegister(const char* name, const char*, uint32_t flags, const char* value,
        bool(__cdecl*)(void*, const char*, const char*, void*), uint32_t category, bool, void*, bool)
    {
        g_registered.push_back(std::string(name) + "=" + value + " " + std::to_string(flags) + "/" + std::to_string(category));
        return &g_fakeCvar;
    }

    bool __fastcall MockSet(void* cvar, void*, const char* value, bool runChanged, bool, bool, bool)
    {
        g_setValues.push_back(value);
        return cvar == &g_fakeCvar && runChanged && CameraFovChanged(cvar, "", value, nullptr);
    }

    void __cdecl MockConsole(const char*, int, ...)
    {
        ++g_consoleLines;
    }

    void UseMocks()
    {
        uint8_t* camera = g_camera.data();
        memcpy(g_worldFrame.data() + Client::WorldActiveCamera, &camera, sizeof(camera));
        g_worldFramePointer = g_worldFrame.data();
        g_worldFrameAddress = reinterpret_cast<uintptr_t>(&g_worldFramePointer);
        g_registerCvar = &MockRegister;
        g_setCvar = reinterpret_cast<SetCvarFn>(&MockSet);
        g_consoleWrite = &MockConsole;
    }

    void ZoomTests()
    {
        PatchConfig config;
        config.cameraZoom = 80;
        CameraZoomPatch().configure(config);
        Check(g_cameraZoomMax == 80.0f && g_cameraZoomHalf == 40.0f,
            "Camera Zoom: an 80-yard setting caps at 80 and the slider factor multiplies 40");
        Patch zoom = CameraZoomPatch();
        Edit edit;
        zoom.sites[1].build(nullptr, edit);
        uint32_t address = 0;
        memcpy(&address, edit.bytes.data() + 2, 4);
        Check(edit.offset == 8 && edit.bytes.size() == 9 && edit.bytes[0] == 0xD8 && edit.bytes[1] == 0x0D
            && address == reinterpret_cast<uintptr_t>(&g_cameraZoomHalf) && edit.bytes[8] == 0x90,
            "Camera Zoom: the factor multiplies the DLL's half distance, in the same 9 bytes");
    }

    void FovTests()
    {
        float radians = 1.0f;
        Check(ParseFovDegrees("110", radians) && Near(radians, 110.0f * Client::RadiansPerDegree), "cameraFov reads degrees");
        Check(ParseFovDegrees("72.5", radians) && Near(radians, 72.5f * Client::RadiansPerDegree), "cameraFov takes fractions");
        radians = 1.0f;
        Check(!ParseFovDegrees("200", radians) && !ParseFovDegrees("10", radians) && !ParseFovDegrees("wide", radians)
            && !ParseFovDegrees(nullptr, radians) && radians == 1.0f, "cameraFov refuses out-of-range and non-numbers, keeping the angle");

        UseMocks();
        g_consoleLines = 0;
        Check(CameraFovChanged(nullptr, "90", "120", nullptr) && Near(CameraAngle(), 120.0f * Client::RadiansPerDegree)
            && Near(g_cameraFovRadians, CameraAngle()), "a cameraFov change turns the active camera at once");
        Check(!CameraFovChanged(nullptr, "120", "300", nullptr) && Near(CameraAngle(), 120.0f * Client::RadiansPerDegree)
            && g_consoleLines == 1, "an out-of-range cameraFov is refused with the range printed");
        g_worldFramePointer = nullptr;
        Check(CameraFovChanged(nullptr, "120", "100", nullptr) && Near(g_cameraFovRadians, 100.0f * Client::RadiansPerDegree),
            "with no world yet, cameraFov still sets the angle the camera will be created with");

        PatchConfig config;
        config.cameraFov = 105;
        CameraFovPatch().configure(config);
        UseMocks();
        g_registerCameraCvars = reinterpret_cast<void*>(&StockRegister);
        ForgetCameraFovStart();
        g_registered.clear();
        g_setValues.clear();
        g_stockRegistered = 0;
        Check(Near(g_cameraFovRadians, 105.0f * Client::RadiansPerDegree), "Field of View creates the camera at the launcher's angle");
        CameraRegisterCvars();
        Check(g_stockRegistered == 1 && g_registered.size() == 1 && g_registered[0] == "cameraFov=105 16/5"
            && g_setValues.size() == 1 && g_setValues[0] == "105" && Near(CameraAngle(), 105.0f * Client::RadiansPerDegree),
            "the stock camera settings register first, then cameraFov, set to the launcher's angle");
        CameraFovChanged(nullptr, "105", "130", nullptr);
        CameraRegisterCvars();
        Check(g_stockRegistered == 2 && g_registered.size() == 2 && g_setValues.size() == 1
            && Near(CameraAngle(), 130.0f * Client::RadiansPerDegree),
            "registering again (the next login) keeps an angle changed in game");

        g_worldFrameAddress = Client::WorldFrame;
        g_registerCvar = reinterpret_cast<RegisterCvarFn>(Client::RegisterCvar);
        g_setCvar = reinterpret_cast<SetCvarFn>(Client::SetCvar);
        g_consoleWrite = reinterpret_cast<ConsoleWriteFn>(Client::ConsoleWrite);
        g_registerCameraCvars = nullptr;
        ForgetCameraFovStart();
    }
}

void CameraTests()
{
    ZoomTests();
    FovTests();
}
