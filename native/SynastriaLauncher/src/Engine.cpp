#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "Engine.h"
#include "Patch.h"
#include "Settings.h"

namespace
{
    struct Region
    {
        uint8_t* start;
        size_t size;
    };

    class Image
    {
    public:
        explicit Image(uint8_t* base)
        {
            auto* nt = reinterpret_cast<IMAGE_NT_HEADERS32*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
            IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(nt);
            for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section)
            {
                m_regions.push_back({ base + section->VirtualAddress, section->Misc.VirtualSize });
            }
        }

        std::vector<uint8_t*> FindAll(const Bytes& pattern) const
        {
            std::vector<uint8_t*> found;
            size_t length = pattern.size();
            for (const Region& region : m_regions)
            {
                uint8_t* last = region.start + (region.size >= length ? region.size - length : 0);
                uint8_t* p = region.size >= length && length > 0 ? region.start : last + 1;
                while (p <= last)
                {
                    p = static_cast<uint8_t*>(memchr(p, pattern[0], static_cast<size_t>(last - p) + 1));
                    if (p == nullptr)
                    {
                        break;
                    }
                    if (memcmp(p, pattern.data(), length) == 0)
                    {
                        found.push_back(p);
                    }
                    ++p;
                }
            }
            return found;
        }

    private:
        std::vector<Region> m_regions;
    };

    // Every write keeps the bytes it replaced, so a patch that fails halfway can be put back.
    class Writes
    {
    public:
        void Write(uint8_t* at, const uint8_t* data, size_t size)
        {
            m_undo.push_back({ at, Bytes(at, at + size) });
            Store(at, data, size);
        }

        void Rollback()
        {
            for (auto it = m_undo.rbegin(); it != m_undo.rend(); ++it)
            {
                Store(it->first, it->second.data(), it->second.size());
            }
            m_undo.clear();
        }

    private:
        static void Store(uint8_t* at, const uint8_t* data, size_t size)
        {
            DWORD old = 0;
            VirtualProtect(at, size, PAGE_EXECUTE_READWRITE, &old);
            memcpy(at, data, size);
            VirtualProtect(at, size, old, &old);
            FlushInstructionCache(GetCurrentProcess(), at, size);
        }

        std::vector<std::pair<uint8_t*, Bytes>> m_undo;
    };

    std::string Describe(const Site& site, const std::string& problem)
    {
        return std::string(site.name) + ": " + problem;
    }

    PatchReport Apply(const Image& image, const Patch& patch, const PatchConfig& config)
    {
        Writes writes;
        int applied = 0;
        int pending = 0;
        std::vector<std::string> problems;
        patch.configure(config);
        for (const Site& site : patch.sites)
        {
            std::vector<uint8_t*> found = image.FindAll(site.find);
            if (found.size() == 1)
            {
                Edit edit;
                std::string problem = site.build ? site.build(found[0], edit) : "";
                edit = site.build ? edit : Edit { 0, site.swap };
                uint8_t* at = found[0] + edit.offset;
                bool same = problem.empty() && memcmp(at, edit.bytes.data(), edit.bytes.size()) == 0;
                if (!problem.empty())
                {
                    problems.push_back(Describe(site, problem));
                }
                else if (!same)
                {
                    writes.Write(at, edit.bytes.data(), edit.bytes.size());
                }
                pending += problem.empty() && !same ? 1 : 0;
                applied += same ? 1 : 0;
            }
            else if (found.empty() && !site.swap.empty() && image.FindAll(site.swap).size() == 1)
            {
                ++applied;
            }
            else
            {
                problems.push_back(Describe(site, "the game's code was found " + std::to_string(found.size()) + " times, not once"));
            }
        }
        PatchReport report { patch.id, Outcome::Applied, "" };
        if (!problems.empty())
        {
            writes.Rollback();
            report.outcome = Outcome::Skipped;
            for (const std::string& problem : problems)
            {
                report.detail += (report.detail.empty() ? "" : "; ") + problem;
            }
        }
        else
        {
            report.outcome = pending == 0 ? Outcome::AlreadyOn : Outcome::Applied;
        }
        return report;
    }

    std::vector<DamageTier> ParseTiers(const std::string& text)
    {
        std::vector<DamageTier> tiers;
        const char* p = text.c_str();
        bool valid = true;
        while (*p && valid)
        {
            char* end = nullptr;
            unsigned long amount = strtoul(p, &end, 10);
            valid = end != p && *end == '=' && amount > 0 && amount <= 0x7FFFFFFF;
            p = valid ? end + 1 : p;
            unsigned long rgb = valid ? strtoul(p, &end, 16) : 0;
            valid = valid && end - p == 6;
            p = valid ? end : p;
            if (valid)
            {
                tiers.push_back({ static_cast<uint32_t>(amount), static_cast<uint32_t>(rgb) });
            }
            p += valid && *p == ',' ? 1 : 0;
        }
        std::sort(tiers.begin(), tiers.end(), [](const DamageTier& a, const DamageTier& b) { return a.amount > b.amount; });
        return valid && !tiers.empty() && tiers.size() <= MaxDamageTiers ? tiers : std::vector<DamageTier>();
    }

    bool Has(const PatchConfig& config, const char* id)
    {
        return std::find(config.on.begin(), config.on.end(), id) != config.on.end();
    }
}

PatchConfig ReadConfig(const std::string& iniPath)
{
    PatchConfig config;
    for (const Patch& patch : Patches())
    {
        if (GetPrivateProfileIntA("Patches", patch.id, 0, iniPath.c_str()) != 0)
        {
            config.on.push_back(patch.id);
        }
    }
    int distance = static_cast<int>(GetPrivateProfileIntA("Settings", "NameplateDistance", DefaultNameplateDistance, iniPath.c_str()));
    int spacing = static_cast<int>(GetPrivateProfileIntA("Settings", "StackSpacing", DefaultStackSpacing, iniPath.c_str()));
    config.nameplateDistance = std::clamp(distance, MinNameplateDistance, MaxNameplateDistance);
    config.stackSpacing = std::clamp(spacing, MinStackSpacing, MaxStackSpacing);
    int zoom = static_cast<int>(GetPrivateProfileIntA("Settings", "CameraZoom", DefaultCameraZoom, iniPath.c_str()));
    int fov = static_cast<int>(GetPrivateProfileIntA("Settings", "CameraFov", DefaultCameraFov, iniPath.c_str()));
    config.cameraZoom = std::clamp(zoom, MinCameraZoom, MaxCameraZoom);
    config.cameraFov = std::clamp(fov, MinCameraFov, MaxCameraFov);
    char colors[512];
    GetPrivateProfileStringA("Settings", "DamageColors", DefaultDamageColors, colors, sizeof(colors), iniPath.c_str());
    config.damageColors = ParseTiers(colors);
    return config;
}

std::vector<PatchReport> ApplyPatches(uint8_t* base, const PatchConfig& config)
{
    Image image(base);
    std::vector<PatchReport> reports;
    for (const Patch& patch : Patches())
    {
        bool badColors = strcmp(patch.id, "DamageColors") == 0 && config.damageColors.empty();
        if (Has(config, patch.id) && badColors)
        {
            reports.push_back({ patch.id, Outcome::Skipped, "the damage color tiers in the settings file aren't valid" });
        }
        else if (Has(config, patch.id))
        {
            reports.push_back(Apply(image, patch, config));
        }
    }
    return reports;
}

PatchReport ApplyPatch(uint8_t* base, const Patch& patch, const PatchConfig& config)
{
    return Apply(Image(base), patch, config);
}

void WriteStatus(const std::string& path, const std::vector<PatchReport>& reports)
{
    FILE* file = nullptr;
    if (fopen_s(&file, path.c_str(), "w") == 0 && file != nullptr)
    {
        SYSTEMTIME t;
        GetLocalTime(&t);
        fprintf(file, "[Run]\nTime=%04d-%02d-%02dT%02d:%02d:%02d\n\n[Patches]\n",
            t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
        for (const PatchReport& report : reports)
        {
            const char* outcome = report.outcome == Outcome::Applied ? "Applied"
                : report.outcome == Outcome::AlreadyOn ? "AlreadyOn" : "Skipped";
            fprintf(file, "%s=%s%s%s\n", report.id.c_str(), outcome, report.detail.empty() ? "" : ": ", report.detail.c_str());
        }
        fclose(file);
    }
}
