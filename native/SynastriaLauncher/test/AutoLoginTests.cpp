// Unit tests for auto login: reading the launcher's environment value and the game's arguments, and the Lua script
// (details only as decimal escapes, the login and character steps in it).
#include <string>
#include "../src/AutoLogin.h"
#include "NativeTests.h"

namespace
{
    bool Contains(const std::string& text, const std::string& part)
    {
        return text.find(part) != std::string::npos;
    }
}

void AutoLoginTests()
{
    // The launcher's value: account, password and character.
    LoginRequest launcher = ParseLoginEnvironment(L"Expulsion\x1Fhunter2\x1FLich");
    Check(launcher.account == "Expulsion" && launcher.password == "hunter2" && launcher.character == "Lich" && launcher.Valid(),
        "login environment: account, password and character");

    // No character, a password holding odd characters, and non-ASCII text in UTF-8.
    LoginRequest noCharacter = ParseLoginEnvironment(L"Acc\x1Fp=a\"s s\\\x1F");
    Check(noCharacter.password == "p=a\"s s\\" && noCharacter.character.empty() && noCharacter.Valid(),
        "login environment: empty character, punctuation kept");
    LoginRequest unicode = ParseLoginEnvironment(L"Acc\x1Fpw\x1F\x00C9lise");
    Check(unicode.character == "\xC3\x89lise", "login environment: non-ASCII names in UTF-8");

    // A malformed value, or no password, logs in as nobody.
    Check(!ParseLoginEnvironment(L"Acc\x1Fpw").Valid() && !ParseLoginEnvironment(L"").Valid()
        && !ParseLoginEnvironment(L"Acc\x1F\x1FLich").Valid(), "login environment: malformed or passwordless is not valid");

    // The game's arguments, names in any case, others ignored.
    const wchar_t* args[] = { L"WowExt.exe", L"-console", L"-LOGIN", L"Expulsion", L"-password", L"hunter2", L"-Character", L"Lich" };
    LoginRequest fromArgs = ParseLoginArguments(8, args);
    Check(fromArgs.account == "Expulsion" && fromArgs.password == "hunter2" && fromArgs.character == "Lich",
        "login arguments: -login, -password and -character in any case");
    const wchar_t* missing[] = { L"WowExt.exe", L"-login", L"Expulsion", L"-password" };
    Check(!ParseLoginArguments(4, missing).Valid(), "login arguments: a flag with no value after it is ignored");
    const wchar_t* none[] = { L"WowExt.exe" };
    Check(!ParseLoginArguments(1, none).Valid(), "login arguments: none given");

    // The script carries the details only as escapes, and runs the client's login and character calls.
    std::string script = AutoLoginScript(LoginRequest { "Acc", "pw\"x", "Lich" });
    Check(Contains(script, "\"\\065\\099\\099\"") && Contains(script, "\"\\112\\119\\034\\120\"")
        && !Contains(script, "pw\"x"), "login script: details as decimal escapes, quotes included");
    Check(Contains(script, "DefaultServerLogin(account, password)") && Contains(script, "SelectCharacter(pick)")
        && Contains(script, "EnterWorld()") && Contains(script, "IsShiftKeyDown()"),
        "login script: logs in, enters the world, and stops while Shift is held");

    // The character can carry details (class, race, faction, level), and only a single match is entered.
    Check(Contains(script, "local function PickCharacter(wanted)") && Contains(script, "matches == 1 and pick or nil")
        && Contains(script, "[\"blood elf\"] = true") && script.find("PickCharacter(wanted)") < script.find("PickCharacter(character)"),
        "login script: picks by name with optional details, only a single match, defined before use");
}
