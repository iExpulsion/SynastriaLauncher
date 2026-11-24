#pragma once
#include <string>
#include "Patch.h"

// Auto login: logs in on the login screen and, with a character named, enters the world as that character. The
// details come from the launcher (an environment variable it sets for the game, cleared once read) or from the
// game's own command line (-login, -password, -character, the names AwesomeWotLK uses). On the first interface
// frame, a short Lua script starts that waits for the login screen and calls the client's own DefaultServerLogin,
// then SelectCharacter and EnterWorld. Holding Shift stops it. It runs once per game start, so logging out or a
// disconnect leaves the player at the screens as usual.

struct LoginRequest
{
    std::string account;
    std::string password;
    std::string character;

    bool Valid() const { return !account.empty() && !password.empty(); }
};

constexpr const wchar_t* LoginEnvironmentName = L"SYNASTRIA_LAUNCHER_LOGIN";
constexpr wchar_t LoginSeparator = L'\x1F';

// The launcher's value: account, password and character separated by LoginSeparator.
LoginRequest ParseLoginEnvironment(const std::wstring& value);

// The game's arguments: -login Name -password Secret [-character Name], names in any case.
LoginRequest ParseLoginArguments(int count, const wchar_t* const* arguments);

// The Lua the login screen runs, with every detail as a decimal-escaped string.
std::string AutoLoginScript(const LoginRequest& request);

// The detour on the client's per-frame update, applied only when there's something to log in with.
Patch AutoLoginPatch(const LoginRequest& request);

extern bool g_autoLoginPending;
extern void* g_fireOnUpdateResume;

extern "C" void __cdecl AutoLoginTick();
extern "C" void AutoLoginFireOnUpdate();
