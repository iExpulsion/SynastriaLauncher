#include <cstdlib>
#include <cstring>
#include "Patch.h"

Bytes Hex(const char* hex)
{
    Bytes bytes;
    const char* p = hex;
    while (*p)
    {
        char* end = nullptr;
        unsigned long value = strtoul(p, &end, 16);
        if (end != p)
        {
            bytes.push_back(static_cast<uint8_t>(value));
            p = end;
        }
        else
        {
            ++p;
        }
    }
    return bytes;
}

Bytes U32(uint32_t value)
{
    Bytes bytes(4);
    memcpy(bytes.data(), &value, 4);
    return bytes;
}

Bytes F32(float value)
{
    Bytes bytes(4);
    memcpy(bytes.data(), &value, 4);
    return bytes;
}

Bytes operator+(Bytes left, const Bytes& right)
{
    left.insert(left.end(), right.begin(), right.end());
    return left;
}

Site Swap(const char* name, Bytes find, Bytes replace)
{
    return Site { name, std::move(find), std::move(replace), nullptr };
}

Site Code(const char* name, Bytes find, Builder build)
{
    return Site { name, std::move(find), {}, std::move(build) };
}

namespace
{
    constexpr uint8_t OpCall = 0xE8;
    constexpr uint8_t OpJump = 0xE9;
    constexpr uint8_t OpPush = 0x68;
    constexpr uint8_t OpNop = 0x90;

    uint32_t Read32(const uint8_t* at)
    {
        uint32_t value = 0;
        memcpy(&value, at, 4);
        return value;
    }

    // What the instruction at `at` refers to, if it's a call, jump or push of an address.
    void* Referenced(const uint8_t* at)
    {
        uintptr_t next = reinterpret_cast<uintptr_t>(at) + 5;
        return at[0] == OpCall || at[0] == OpJump ? reinterpret_cast<void*>(next + Read32(at + 1))
            : at[0] == OpPush ? reinterpret_cast<void*>(static_cast<uintptr_t>(Read32(at + 1)))
            : nullptr;
    }

    std::string Remember(const uint8_t* at, void** original)
    {
        void* found = Referenced(at);
        bool agrees = original == nullptr || *original == nullptr || *original == found;
        bool ok = original == nullptr || (found != nullptr && agrees);
        if (ok && original != nullptr)
        {
            *original = found;
        }
        return ok ? "" : found == nullptr ? "the matched code has no call there" : "it calls something else than the other sites";
    }

    Builder Branch(uint8_t opcode, size_t offset, const void* target, void** original, size_t nops)
    {
        return [=](uint8_t* match, Edit& edit)
        {
            uint8_t* at = match + offset;
            std::string problem = Remember(at, original);
            uint32_t rel = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(target) - reinterpret_cast<uintptr_t>(at + 5));
            edit.offset = offset;
            edit.bytes = Bytes { opcode } + U32(rel) + Bytes(nops, OpNop);
            return problem;
        };
    }
}

Builder CallTo(size_t at, const void* target, void** original, size_t nops)
{
    return Branch(OpCall, at, target, original, nops);
}

Builder JumpTo(size_t at, const void* target, size_t nops)
{
    return Branch(OpJump, at, target, nullptr, nops);
}

Builder PushAddress(size_t offset, const void* target, void** original)
{
    return [=](uint8_t* match, Edit& edit)
    {
        std::string problem = Remember(match + offset, original);
        edit.offset = offset;
        edit.bytes = Bytes { OpPush } + U32(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(target)));
        return problem;
    };
}

Builder WriteBytes(size_t offset, std::function<Bytes()> bytes)
{
    return [=](uint8_t*, Edit& edit)
    {
        edit.offset = offset;
        edit.bytes = bytes();
        return std::string();
    };
}

const std::vector<Patch>& Patches()
{
    static const std::vector<Patch> all =
    {
        InterfaceEditPatch(),
        NoAutoAfkPatch(),
        NoMailDelayPatch(),
        InteractKeyPatch(),
        CursorOptimizationsPatch(),
        CameraZoomPatch(),
        CameraFovPatch(),
        GraphicsLimitsPatch(),
        NoZoomFadePatch(),
        HdPortraitsPatch(),
        NameplateDistancePatch(),
        NameplateSortPatch(),
        NameplateStackPatch(),
        NameplateUnitsPatch(),
        ShortNumbersPatch(),
        DamageColorsPatch(),
        GroundTargetsPatch(),
    };
    return all;
}
