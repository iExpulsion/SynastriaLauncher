// Unit tests for SynastriaLauncher.dll's patches: each patch's logic, the assembly shims' calling conventions, and (given an
// unpatched WowExt.exe) that every site is found once and ends up pointing where it should. Prints one line per check
// and exits nonzero on any failure. Run by the launcher's tests through PatcherTests.exe.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>
#include "../src/Client.h"
#include "../src/DamageColors.h"
#include "../src/GroundTargets.h"
#include "../src/Engine.h"
#include "../src/GraphicsLimits.h"
#include "../src/HdPortraits.h"
#include "../src/ImageFile.h"
#include "../src/NameplateSort.h"
#include "../src/NameplateStack.h"
#include "../src/Patch.h"
#include "../src/ShortNumbers.h"
#include "../src/CursorOptimizations.h"
#include "../src/InteractKey.h"
#include "../src/AutoLogin.h"
#include "NativeTests.h"

#pragma warning(disable : 4731)

namespace
{
    int g_failures = 0;
}

void Check(bool ok, const std::string& what)
{
    printf("%s  %s\n", ok ? "pass" : "FAIL", what.c_str());
    g_failures += ok ? 0 : 1;
}

namespace
{
    std::string Short(int value, bool heal)
    {
        char text[32] = "unchanged";
        bool shortened = ShortenNumber(value, heal, text, sizeof(text));
        return shortened ? text : "unchanged";
    }

    std::string g_printed;

    int __cdecl FakePrintf(char* buffer, uint32_t size, const char* format, ...)
    {
        va_list args;
        va_start(args, format);
        int length = vsnprintf(buffer, size, format, args);
        va_end(args);
        g_printed = format;
        return length;
    }

    const uint32_t* g_seenColor = nullptr;

    int __cdecl FakeCreateText(int, int, int, const uint32_t* color, int)
    {
        g_seenColor = color;
        return 7;
    }

    // Calls a damage text shim the way the client's call site does: with ebp on the caller's frame and five
    // arguments, the fourth being the color pointer. Reports whether esp and the saved registers came back intact.
    const uint32_t* CallDamageShim(void (*shim)(), int frameOffset, int amount, const uint32_t* color, bool& intact)
    {
        uint32_t frame[8] = {};
        frame[frameOffset / 4] = static_cast<uint32_t>(amount);
        g_seenColor = nullptr;
        g_createDamageText = reinterpret_cast<void*>(&FakeCreateText);
        uint32_t* framePointer = frame;
        uint32_t ok = 0;
        __asm
        {
            mov ecx, framePointer
            mov edx, color
            mov eax, shim
            push ebx
            push esi
            push edi
            push ebp
            mov ebx, 0B0B0B0B0h
            mov esi, 5E5E5E5Eh
            mov ebp, ecx
            push 0
            push edx
            push 0
            push 2
            push 1234h
            mov edi, esp
            call eax
            xor edx, edx
            cmp edi, esp
            jne bad
            cmp ebx, 0B0B0B0B0h
            jne bad
            cmp esi, 5E5E5E5Eh
            jne bad
            cmp eax, 7
            jne bad
            mov edx, 1
        bad:
            add esp, 20
            pop ebp
            pop edi
            pop esi
            pop ebx
            mov ok, edx
        }
        intact = ok == 1;
        return g_seenColor;
    }

    uint32_t ColorOf(const uint32_t* color)
    {
        return color != nullptr ? *color : 0;
    }

    void ShortNumberTests()
    {
        // Values under 10,000 are left to the client's own printf.
        Check(Short(9999, false) == "unchanged", "9999 is printed as it is");
        Check(Short(-50000, false) == "unchanged", "negative values are printed as they are");
        // Thousands, millions and billions, one decimal, with ".0" dropped.
        Check(Short(10000, false) == "10K", "10000 -> 10K");
        Check(Short(12345, false) == "12.3K", "12345 -> 12.3K");
        Check(Short(500000, false) == "500K", "500000 -> 500K");
        Check(Short(999949, false) == "999.9K", "999949 -> 999.9K");
        Check(Short(999950, false) == "1M", "999950 -> 1M (rounds into millions)");
        Check(Short(1234567, false) == "1.2M", "1234567 -> 1.2M");
        Check(Short(999950000, false) == "1B", "999950000 -> 1B");
        Check(Short(2000000000, false) == "2B", "2000000000 -> 2B");
        // Heals get a plus sign.
        Check(Short(12345, true) == "+12.3K", "heal 12345 -> +12.3K");
        Check(Short(2000000, true) == "+2M", "heal 2000000 -> +2M");

        // The entry points pass small values to the client's printf with its own format, and print big ones.
        g_shortNumbersPrintf = &FakePrintf;
        char buffer[32];
        int length = ShortDamage(buffer, sizeof(buffer), "%d", 42);
        Check(std::string(buffer) == "42" && g_printed == "%d" && length == 2, "ShortDamage passes 42 to the client's printf");
        g_printed.clear();
        length = ShortHeal(buffer, sizeof(buffer), "+%d", 1234567);
        Check(std::string(buffer) == "+1.2M" && g_printed.empty() && length == 5, "ShortHeal prints +1.2M itself");
    }

    void DamageColorTests()
    {
        SetDamageTiers({ { 5000000, 0xC040FF }, { 1000000, 0xFF3030 }, { 250000, 0xFFD700 } });
        // The first tier an amount reaches wins; below the lowest tier the game's color stays.
        Check(PickDamageColor(100000) == nullptr, "100000 keeps the game's color");
        Check(ColorOf(PickDamageColor(250000)) == 0xFFFFD700, "250000 (exactly a tier) -> FFD700");
        Check(ColorOf(PickDamageColor(300000)) == 0xFFFFD700, "300000 -> FFD700");
        Check(ColorOf(PickDamageColor(2000000)) == 0xFFFF3030, "2000000 -> FF3030");
        Check(ColorOf(PickDamageColor(9000000)) == 0xFFC040FF, "9000000 -> C040FF");
        // All ten tiers are used, the lowest included.
        std::vector<DamageTier> ten;
        for (uint32_t i = 10; i >= 1; --i)
        {
            ten.push_back({ i * 100000, i });
        }
        SetDamageTiers(ten);
        Check(ColorOf(PickDamageColor(100000)) == 0xFF000001 && ColorOf(PickDamageColor(1500000)) == 0xFF00000A,
            "ten tiers: the lowest and highest both apply");
        SetDamageTiers({});
        Check(PickDamageColor(9000000) == nullptr, "no tiers keeps every color");

        // The shims read the amount from the caller's frame, swap the color argument and keep the stack.
        SetDamageTiers({ { 1000000, 0xFF3030 }, { 250000, 0xFFD700 } });
        uint32_t original = 0xFFFFDE00;
        bool intact = false;
        Check(ColorOf(CallDamageShim(&DamageTextFromSecondArg, 12, 300000, &original, intact)) == 0xFFFFD700 && intact,
            "second-argument shim: 300000 -> FFD700, stack and registers kept");
        Check(CallDamageShim(&DamageTextFromSecondArg, 12, 100000, &original, intact) == &original && intact,
            "second-argument shim: 100000 keeps the original color pointer");
        Check(ColorOf(CallDamageShim(&DamageTextFromFirstArg, 8, 2000000, &original, intact)) == 0xFFFF3030 && intact,
            "first-argument shim: 2000000 -> FF3030");
    }

    std::vector<uint8_t*> FindAll(uint8_t* image, uint32_t size, const Bytes& pattern)
    {
        std::vector<uint8_t*> found;
        for (uint32_t i = 0; i + pattern.size() <= size; ++i)
        {
            if (image[i] == pattern[0] && memcmp(image + i, pattern.data(), pattern.size()) == 0)
            {
                found.push_back(image + i);
            }
        }
        return found;
    }

    uint32_t Read32(const uint8_t* at)
    {
        uint32_t value = 0;
        memcpy(&value, at, 4);
        return value;
    }

    // Where the call, jump or push at `at` goes.
    uint8_t* Target(uint8_t* at)
    {
        return at[0] == 0x68 ? reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(Read32(at + 1)))
            : at + 5 + static_cast<int32_t>(Read32(at + 1));
    }

    uint32_t Checksum(const uint8_t* image, uint32_t size)
    {
        uint32_t sum = 2166136261u;
        for (uint32_t i = 0; i < size; ++i)
        {
            sum = (sum ^ image[i]) * 16777619u;
        }
        return sum;
    }

    // On the real exe: every site is found exactly once, every patch applies without the exe's patch section, each
    // site that leads into this DLL leads to the right place, and the client functions it hands on to are the ones
    // the original code called. A second run patches nothing.
    void SiteTests(const char* exePath)
    {
        uint32_t size = 0;
        uint8_t* image = MapImageFile(exePath, size);
        Check(image != nullptr, "the exe maps");
        std::map<std::string, uint8_t*> at;
        int foundOnce = 0;
        int sites = 0;
        for (const Patch& patch : Patches())
        {
            for (const Site& site : patch.sites)
            {
                std::vector<uint8_t*> found = image != nullptr ? FindAll(image, size, site.find) : std::vector<uint8_t*>();
                at[std::string(patch.id) + "/" + site.name] = found.size() == 1 ? found[0] : nullptr;
                foundOnce += found.size() == 1 ? 1 : 0;
                ++sites;
            }
        }
        Check(sites > 0 && foundOnce == sites, "every site is found exactly once (" + std::to_string(foundOnce) + " of " + std::to_string(sites) + ")");

        auto originalAt = [&](const char* key, size_t offset) { return at[key] != nullptr ? Target(at[key] + offset) : nullptr; };
        uint8_t* printf = originalAt("ShortNumbers/DamageNumber", 12);
        uint8_t* createText = originalAt("DamageColors/DamageText", 5);
        uint8_t* fireOnUpdate = originalAt("NameplateSort/RegisterFireOnUpdate", 6);
        uint8_t* layout = originalAt("NameplateStack/LayoutPass", 8);
        uint8_t* renderTarget = originalAt("HdPortraits/CreateRenderTarget", 3);
        uint8_t* texture = originalAt("HdPortraits/CreateTexture", 3);

        PatchConfig config;
        for (const Patch& patch : Patches())
        {
            config.on.push_back(patch.id);
        }
        config.nameplateDistance = 60;
        config.stackSpacing = 100;
        config.damageColors = { { 250000, 0xFFD700 } };
        std::vector<PatchReport> reports = image != nullptr ? ApplyPatches(image, config) : std::vector<PatchReport>();
        int applied = 0;
        for (const PatchReport& report : reports)
        {
            applied += report.outcome == Outcome::Applied ? 1 : 0;
            if (report.outcome != Outcome::Applied)
            {
                Check(false, report.id + ": " + report.detail);
            }
        }
        Check(applied == static_cast<int>(Patches().size()), "all " + std::to_string(Patches().size()) + " patches apply to the exe without its patch section");

        // Auto login, applied after the catalog's patches as the DLL does: the per-frame update's entry jumps to its
        // tick, which resumes the client's function after the moved prologue.
        Patch autoLogin = AutoLoginPatch(LoginRequest { "Account", "Secret", "" });
        std::vector<uint8_t*> fireEntry = image != nullptr ? FindAll(image, size, autoLogin.sites[0].find) : std::vector<uint8_t*>();
        PatchReport loginReport = image != nullptr ? ApplyPatch(image, autoLogin, config) : PatchReport { "AutoLogin", Outcome::Skipped, "" };
        Check(fireEntry.size() == 1 && loginReport.outcome == Outcome::Applied && fireEntry[0][0] == 0xE9
            && fireEntry[0] + 5 + static_cast<int32_t>(Read32(fireEntry[0] + 1)) == reinterpret_cast<uint8_t*>(&AutoLoginFireOnUpdate)
            && fireEntry[0][5] == 0x90 && g_fireOnUpdateResume == fireEntry[0] + 6 && g_autoLoginPending,
            "Auto Login: the per-frame update's entry jumps to the tick, which resumes after the prologue");
        g_autoLoginPending = false;

        struct Expected
        {
            const char* key;
            size_t offset;
            const void* target;
        };
        const Expected expected[] =
        {
            { "ShortNumbers/DamageNumber", 12, reinterpret_cast<const void*>(&ShortDamage) },
            { "ShortNumbers/DamageNumberOther", 12, reinterpret_cast<const void*>(&ShortDamage) },
            { "ShortNumbers/HealNumber", 12, reinterpret_cast<const void*>(&ShortHeal) },
            { "ShortNumbers/HealNumberOther", 12, reinterpret_cast<const void*>(&ShortHeal) },
            { "DamageColors/DamageText", 5, reinterpret_cast<const void*>(&DamageTextFromSecondArg) },
            { "DamageColors/DamageTextOther", 4, reinterpret_cast<const void*>(&DamageTextFromFirstArg) },
            { "NameplateSort/RegisterFireOnUpdate", 6, reinterpret_cast<const void*>(&SortThenFireOnUpdate) },
            { "NameplateStack/LayoutPass", 8, reinterpret_cast<const void*>(&StackThenLayout) },
            { "NameplateStack/PlateY", 9, reinterpret_cast<const void*>(&LoadStackedY) },
            { "NameplateStack/LayoutFlag", 3, reinterpret_cast<const void*>(&ClearLayoutFlagUnlessMoving) },
            { "HdPortraits/CreateRenderTarget", 3, reinterpret_cast<const void*>(&RenderTargetAt256) },
            { "HdPortraits/CreateTexture", 3, reinterpret_cast<const void*>(&TextureAt256) },
            { "HdPortraits/MaskLookup", 1, reinterpret_cast<const void*>(&PortraitMaskForSize) },
            { "GraphicsLimits/GlueSliderMaximum", 1, reinterpret_cast<const void*>(&SliderMaximum) },
            { "GraphicsLimits/GameSliderMaximum", 1, reinterpret_cast<const void*>(&SliderMaximum) },
            { "CursorOptimizations/CursorUnlock", 7, reinterpret_cast<const void*>(&CursorChanged) },
            { "CursorOptimizations/CursorUpdate", 47, reinterpret_cast<const void*>(&CursorUploadCheck) },
            { "CursorOptimizations/CursorUpdateEx", 47, reinterpret_cast<const void*>(&CursorUploadCheck) },
            { "CursorOptimizations/WindowSetCursor", 15, reinterpret_cast<const void*>(&WindowSetCursor) },
        };
        for (const Expected& site : expected)
        {
            Check(at[site.key] != nullptr && Target(at[site.key] + site.offset) == site.target, std::string(site.key) + " points into this DLL");
        }
        Check(printf != nullptr && reinterpret_cast<uint8_t*>(g_shortNumbersPrintf) == printf, "Short Numbers hands on to the client's printf");
        Check(createText != nullptr && reinterpret_cast<uint8_t*>(g_createDamageText) == createText, "Damage Colors hands on to the client's damage text");
        Check(fireOnUpdate != nullptr && reinterpret_cast<uint8_t*>(g_fireOnUpdate) == fireOnUpdate, "Sorting hands on to FireOnUpdate");
        Check(layout != nullptr && reinterpret_cast<uint8_t*>(g_layoutNameplates) == layout, "Stacking hands on to the layout pass");
        Check(renderTarget != nullptr && reinterpret_cast<uint8_t*>(g_createRenderTarget) == renderTarget, "HD Portraits hands on to the render target call");
        Check(texture != nullptr && reinterpret_cast<uint8_t*>(g_createTexture) == texture, "HD Portraits hands on to the texture call");
        Check(at["HdPortraits/MaskLookup"] != nullptr && g_maskLookupBody == at["HdPortraits/MaskLookup"] + 10,
            "HD Portraits resumes the mask lookup after its first 9 bytes");
        uint8_t* entry = at["GroundTargets/CastSpellByNameEntry"];
        Check(entry != nullptr && entry[0] == 0xE9
            && entry + 5 + static_cast<int32_t>(Read32(entry + 1)) == reinterpret_cast<uint8_t*>(&GroundTargetCastSpellByName)
            && entry[5] == 0x90 && entry[8] == 0x90
            && g_castSpellByName == reinterpret_cast<void*>(&GroundOriginalCastSpellByName)
            && g_castSpellByNameResume == entry + 9,
            "Ground Targets: CastSpellByName's entry jumps to the wrapper, which resumes the client's function after its prologue");
        uint8_t* interact = at["InteractKey/InteractUnitEntry"];
        Check(interact != nullptr && interact[0] == 0xE9
            && interact + 5 + static_cast<int32_t>(Read32(interact + 1)) == reinterpret_cast<uint8_t*>(&InteractKeyInteractUnit)
            && interact[5] == 0x90 && interact[6] == 0x90 && g_interactUnitResume == interact + 7,
            "Interact Key: InteractUnit's entry jumps to the wrapper, which resumes the client's function after its prologue");
        uint8_t* distance = at["NameplateDistance/SquaredDistance"];
        float squared = 0;
        if (distance != nullptr)
        {
            memcpy(&squared, distance + 8, 4);
        }
        Check(squared == 3600.0f, "Nameplate Distance writes 60 yards squared");

        // A second run: byte swaps read as already on, the rest can't find their code, and nothing changes.
        uint32_t before = image != nullptr ? Checksum(image, size) : 0;
        std::vector<PatchReport> again = image != nullptr ? ApplyPatches(image, config) : std::vector<PatchReport>();
        bool expectedOutcomes = again.size() == Patches().size();
        for (size_t i = 0; i < again.size() && i < Patches().size(); ++i)
        {
            bool swapsOnly = true;
            for (const Site& site : Patches()[i].sites)
            {
                swapsOnly = swapsOnly && !site.build;
            }
            bool distanceAgain = again[i].id == "NameplateDistance";
            Outcome want = swapsOnly || distanceAgain ? Outcome::AlreadyOn : Outcome::Skipped;
            expectedOutcomes = expectedOutcomes && again[i].outcome == want;
        }
        Check(expectedOutcomes && image != nullptr && Checksum(image, size) == before,
            "a second run writes nothing: swaps already on, the rest skipped");
    }
}

int main(int argc, char** argv)
{
    ShortNumberTests();
    DamageColorTests();
    NameplateTests();
    VisualTests();
    GroundTests();
    CursorTests();
    InteractTests();
    AutoLoginTests();
    if (argc > 1)
    {
        SiteTests(argv[1]);
    }
    printf("%d failure(s)\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
