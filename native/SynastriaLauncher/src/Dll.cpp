#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <string>
#include "AutoLogin.h"
#include "Engine.h"

namespace
{
    // The launcher's details first (cleared so nothing the game starts inherits them), else the game's arguments.
    LoginRequest ReadLogin()
    {
        LoginRequest request;
        DWORD size = GetEnvironmentVariableW(LoginEnvironmentName, nullptr, 0);
        if (size > 0)
        {
            std::wstring value(size, L'\0');
            value.resize(GetEnvironmentVariableW(LoginEnvironmentName, value.data(), size));
            SetEnvironmentVariableW(LoginEnvironmentName, nullptr);
            request = ParseLoginEnvironment(value);
            SecureZeroMemory(value.data(), value.size() * sizeof(wchar_t));
        }
        else
        {
            int count = 0;
            LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &count);
            if (arguments != nullptr)
            {
                request = ParseLoginArguments(count, arguments);
                LocalFree(arguments);
            }
        }
        return request;
    }
}

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
        uint8_t* base = reinterpret_cast<uint8_t*>(GetModuleHandleA(nullptr));
        std::vector<PatchReport> reports = ApplyPatches(base, config);
        LoginRequest login = ReadLogin();
        if (login.Valid())
        {
            reports.push_back(ApplyPatch(base, AutoLoginPatch(login), config));
        }
        WriteStatus(folder + "SynastriaLauncher.status.ini", reports);
    }
    return TRUE;
}
