#pragma once
#include <cstddef>
#include <cstdint>

// Writes value shortened to one decimal (12.3K, 1.2M, 2B, with a "+" for heals) and drops a trailing ".0".
// Returns false, writing nothing, below 10,000: those are printed as they are.
bool ShortenNumber(int value, bool heal, char* out, size_t size);

using PrintfFunction = int(__cdecl*)(char* buffer, uint32_t size, const char* format, ...);

extern PrintfFunction g_shortNumbersPrintf;

int __cdecl ShortDamage(char* buffer, uint32_t size, const char* format, int value);
int __cdecl ShortHeal(char* buffer, uint32_t size, const char* format, int value);
