#pragma once
#include <cstdint>
#include <string>
#include <vector>

struct DamageTier
{
    uint32_t amount;
    uint32_t rgb;
};

struct PatchConfig
{
    std::vector<std::string> on;
    int nameplateDistance = 0;
    int stackSpacing = 0;
    int cameraZoom = 0;
    int cameraFov = 0;
    std::vector<DamageTier> damageColors;
};

enum class Outcome
{
    Applied,
    AlreadyOn,
    Skipped,
};

struct PatchReport
{
    std::string id;
    Outcome outcome;
    std::string detail;
};

PatchConfig ReadConfig(const std::string& iniPath);

// Applies the switched-on patches to the PE image mapped at base, in catalog order. A patch is written only when
// every part of it matches; otherwise whatever it wrote is put back and it is reported as skipped.
std::vector<PatchReport> ApplyPatches(uint8_t* base, const PatchConfig& config);

struct Patch;

// Applies one patch that isn't in the catalog, the same way.
PatchReport ApplyPatch(uint8_t* base, const Patch& patch, const PatchConfig& config);

void WriteStatus(const std::string& path, const std::vector<PatchReport>& reports);
