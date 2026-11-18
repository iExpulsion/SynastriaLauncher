#pragma once
#include <cstdint>
#include <string>

// Test helper: lays an exe file out in memory the way the Windows loader does (headers, then each section at its
// address). Returns null if the file can't be read. imageSize receives SizeOfImage.
uint8_t* MapImageFile(const std::string& path, uint32_t& imageSize);
