// Test host, never shipped. "PatcherHost <exe> <ini> <image out> <status out>" lays an exe file out in memory the
// way the Windows loader does, runs the patcher on it with a settings file, and saves the resulting image and status.
// "PatcherHost --list" prints every patch's sites and the setting defaults, and "PatcherHost --mask <out>" saves the
// HD portrait mask, so the launcher's tests can compare them with its own patch catalog.
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>
#include "Engine.h"
#include "HdPortraits.h"
#include "ImageFile.h"
#include "Patch.h"
#include "Settings.h"

namespace
{
    std::string HexText(const Bytes& bytes)
    {
        std::string text;
        char part[4];
        for (size_t i = 0; i < bytes.size(); ++i)
        {
            snprintf(part, sizeof(part), i == 0 ? "%02X" : " %02X", bytes[i]);
            text += part;
        }
        return text.empty() ? "-" : text;
    }

    int List()
    {
        for (const Patch& patch : Patches())
        {
            printf("patch %s\n", patch.id);
            for (const Site& site : patch.sites)
            {
                printf("site %s|%s|%s\n", site.name, HexText(site.find).c_str(), HexText(site.swap).c_str());
            }
        }
        printf("default NameplateDistance %d %d %d\n", DefaultNameplateDistance, MinNameplateDistance, MaxNameplateDistance);
        printf("default StackSpacing %d %d %d\n", DefaultStackSpacing, MinStackSpacing, MaxStackSpacing);
        printf("default DamageColors %s %d\n", DefaultDamageColors, static_cast<int>(MaxDamageTiers));
        return 0;
    }

    int Mask(const char* path)
    {
        std::vector<uint8_t> mask(PortraitSize * PortraitSize);
        BuildPortraitMask(mask.data());
        std::ofstream output(path, std::ios::binary);
        output.write(reinterpret_cast<const char*>(mask.data()), static_cast<std::streamsize>(mask.size()));
        return 0;
    }

    int Run(char** argv)
    {
        uint32_t imageSize = 0;
        uint8_t* image = MapImageFile(argv[1], imageSize);
        int code = 2;
        if (image != nullptr)
        {
            std::vector<PatchReport> reports = ApplyPatches(image, ReadConfig(argv[2]));
            std::ofstream output(argv[3], std::ios::binary);
            output.write(reinterpret_cast<const char*>(image), imageSize);
            WriteStatus(argv[4], reports);
            code = 0;
        }
        return code;
    }
}

int main(int argc, char** argv)
{
    bool list = argc == 2 && strcmp(argv[1], "--list") == 0;
    bool mask = argc == 3 && strcmp(argv[1], "--mask") == 0;
    int code = list ? List() : mask ? Mask(argv[2]) : argc == 5 ? Run(argv) : 2;
    if (code == 2)
    {
        printf("usage: PatcherHost <exe> <SynastriaLauncher.ini> <image out> <status out> | --list | --mask <out>\n");
    }
    return code;
}
