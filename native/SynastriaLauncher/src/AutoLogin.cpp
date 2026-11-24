#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cwchar>
#include "AutoLogin.h"
#include "Client.h"

bool g_autoLoginPending = false;
void* g_fireOnUpdateResume = nullptr;

namespace
{
    std::string g_script;

    std::string Utf8(const std::wstring& text)
    {
        int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string result(static_cast<size_t>(size > 0 ? size : 0), '\0');
        if (size > 0)
        {
            WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
        }
        return result;
    }

    std::string LuaString(const std::string& text)
    {
        std::string quoted = "\"";
        char escape[8];
        for (unsigned char c : text)
        {
            snprintf(escape, sizeof(escape), "\\%03u", static_cast<unsigned>(c));
            quoted += escape;
        }
        return quoted + "\"";
    }

    constexpr const char* ScriptBody = R"lua(
local frame = CreateFrame("Frame")
local stage, wait = "login", 0.5
frame:SetScript("OnUpdate", function(self, elapsed)
    wait = wait - elapsed
    if IsShiftKeyDown() then
        self:SetScript("OnUpdate", nil)
    elseif wait <= 0 and stage == "login" and AccountLogin and AccountLogin:IsVisible() then
        DefaultServerLogin(account, password)
        password = nil
        stage, wait = "character", 1
        if character == "" then
            self:SetScript("OnUpdate", nil)
        end
    elseif wait <= 0 and stage == "character" and CharacterSelect and CharacterSelect:IsVisible() and GetNumCharacters() > 0 then
        self:SetScript("OnUpdate", nil)
        local pick = PickCharacter(character)
        if pick then
            SelectCharacter(pick)
            EnterWorld()
        end
    end
end)
)lua";

    // "Name", or "Name (detail, detail)" where each detail is the character's class, race, faction or level, without
    // case. Only a single match is entered; otherwise the player stays at character select.
    constexpr const char* PickBody = R"lua(
local horde = { ["orc"] = true, ["undead"] = true, ["tauren"] = true, ["troll"] = true, ["blood elf"] = true }

local function Fits(detail, race, class, level)
    local faction = race and (horde[race] and "horde" or "alliance")
    return detail == "" or detail == race or detail == class or detail == faction or detail == tostring(level)
end

local function PickCharacter(wanted)
    local name, details = string.match(wanted, "^%s*(.-)%s*%((.-)%)%s*$")
    name = string.lower(name or wanted)
    local pick, matches = nil, 0
    for i = 1, GetNumCharacters() do
        local each, race, class, level = GetCharacterInfo(i)
        local fits = each ~= nil and string.lower(each) == name
        race, class = race and string.lower(race), class and string.lower(class)
        for detail in string.gmatch(details or "", "[^,]+") do
            fits = fits and Fits(string.lower(string.match(detail, "^%s*(.-)%s*$")), race, class, level)
        end
        if fits then
            pick, matches = i, matches + 1
        end
    end
    return matches == 1 and pick or nil
end
)lua";

    using ExecuteFn = void(__cdecl*)(const char* code, const char* chunk, int);

    std::string HookEntry(uint8_t* match, Edit& edit)
    {
        g_fireOnUpdateResume = match + Client::FireOnUpdatePrologue;
        return JumpTo(0, reinterpret_cast<const void*>(&AutoLoginFireOnUpdate), Client::FireOnUpdatePrologue - 5)(match, edit);
    }
}

LoginRequest ParseLoginEnvironment(const std::wstring& value)
{
    LoginRequest request;
    size_t first = value.find(LoginSeparator);
    size_t second = first == std::wstring::npos ? std::wstring::npos : value.find(LoginSeparator, first + 1);
    if (second != std::wstring::npos)
    {
        request.account = Utf8(value.substr(0, first));
        request.password = Utf8(value.substr(first + 1, second - first - 1));
        request.character = Utf8(value.substr(second + 1));
    }
    return request;
}

LoginRequest ParseLoginArguments(int count, const wchar_t* const* arguments)
{
    LoginRequest request;
    for (int i = 0; i + 1 < count; ++i)
    {
        const wchar_t* name = arguments[i];
        std::string value = Utf8(arguments[i + 1]);
        if (_wcsicmp(name, L"-login") == 0)
        {
            request.account = value;
        }
        else if (_wcsicmp(name, L"-password") == 0)
        {
            request.password = value;
        }
        else if (_wcsicmp(name, L"-character") == 0)
        {
            request.character = value;
        }
    }
    return request;
}

std::string AutoLoginScript(const LoginRequest& request)
{
    return "local account, password, character = " + LuaString(request.account) + ", " + LuaString(request.password)
        + ", " + LuaString(request.character) + "\n" + PickBody + ScriptBody;
}

extern "C" void __cdecl AutoLoginTick()
{
    if (g_autoLoginPending)
    {
        g_autoLoginPending = false;
        reinterpret_cast<ExecuteFn>(Client::FrameScriptExecute)(g_script.c_str(), "SynastriaLauncher", 0);
        SecureZeroMemory(g_script.data(), g_script.size());
        g_script.clear();
    }
}

// Runs the tick, then the moved prologue (push ebp; mov ebp, esp; fld dword ptr [ebp+0x14]) and the rest of the
// client's function.
extern "C" __declspec(naked) void AutoLoginFireOnUpdate()
{
    __asm
    {
        pushad
        pushfd
        call AutoLoginTick
        popfd
        popad
        push ebp
        mov ebp, esp
        fld dword ptr [ebp + 0x14]
        jmp dword ptr [g_fireOnUpdateResume]
    }
}

Patch AutoLoginPatch(const LoginRequest& request)
{
    return Patch
    {
        "AutoLogin",
        {
            Code("FireOnUpdateEntry", Hex("55 8B EC D9 45 14 56 8B 35 A8 99 B4 00 51 8B CE D9 1C 24 E8"), HookEntry),
        },
        [request](const PatchConfig&)
        {
            g_fireOnUpdateResume = nullptr;
            g_script = AutoLoginScript(request);
            g_autoLoginPending = true;
        },
    };
}
