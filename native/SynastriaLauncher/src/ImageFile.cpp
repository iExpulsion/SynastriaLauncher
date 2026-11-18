#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include <fstream>
#include <iterator>
#include <vector>
#include "ImageFile.h"

uint8_t* MapImageFile(const std::string& path, uint32_t& imageSize)
{
    std::ifstream input(path, std::ios::binary);
    std::vector<uint8_t> file((std::istreambuf_iterator<char>(input)), {});
    uint8_t* image = nullptr;
    imageSize = 0;
    if (file.size() > sizeof(IMAGE_DOS_HEADER))
    {
        auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(file.data() + reinterpret_cast<const IMAGE_DOS_HEADER*>(file.data())->e_lfanew);
        imageSize = nt->OptionalHeader.SizeOfImage;
        image = static_cast<uint8_t*>(VirtualAlloc(nullptr, imageSize, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
        memcpy(image, file.data(), nt->OptionalHeader.SizeOfHeaders);
        const IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(nt);
        for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section)
        {
            DWORD size = section->SizeOfRawData < section->Misc.VirtualSize ? section->SizeOfRawData : section->Misc.VirtualSize;
            memcpy(image + section->VirtualAddress, file.data() + section->PointerToRawData, size);
        }
    }
    return image;
}
