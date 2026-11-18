#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include "Engine.h"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        char path[MAX_PATH];
        DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
        std::string folder(path, length);
        folder.erase(folder.find_last_of("\\/") + 1);
        PatchConfig config = ReadConfig(folder + "SynastriaLauncher.ini");
        std::vector<PatchReport> reports = ApplyPatches(reinterpret_cast<uint8_t*>(GetModuleHandleA(nullptr)), config);
        WriteStatus(folder + "SynastriaLauncher.status.ini", reports);
    }
    return TRUE;
}
