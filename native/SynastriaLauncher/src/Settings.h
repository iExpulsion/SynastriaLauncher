#pragma once

// Setting defaults and limits, the same as the launcher's PatchSettings (a test checks they match).
constexpr int DefaultNameplateDistance = 60;
constexpr int MinNameplateDistance = 20;
constexpr int MaxNameplateDistance = 100;
constexpr int DefaultStackSpacing = 100;
constexpr int MinStackSpacing = 40;
constexpr int MaxStackSpacing = 150;
constexpr int DefaultCameraZoom = 50;
constexpr int MinCameraZoom = 30;
constexpr int MaxCameraZoom = 100;
constexpr int DefaultCameraFov = 90;
constexpr int MinCameraFov = 60;
constexpr int MaxCameraFov = 150;
constexpr const char* DefaultDamageColors = "5000000=C040FF,1000000=FF3030,250000=FFD700";
constexpr size_t MaxDamageTiers = 10;
