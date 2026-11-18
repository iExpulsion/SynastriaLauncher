// Patches that change a few bytes of the game's code or data in place.
#include "Client.h"
#include "Patch.h"

namespace
{
    constexpr uint8_t JeShort = 0x74;
    constexpr uint8_t JneShort = 0x75;
    constexpr uint8_t JgShort = 0x7F;
    constexpr uint8_t JmpShort = 0xEB;
    constexpr uint8_t Nop = 0x90;

    Bytes Op(uint8_t opcode)
    {
        return Bytes { opcode };
    }

    Bytes MovEax(uint32_t value)
    {
        return Op(0xB8) + U32(value);
    }
}

// Lets the game load modified interface files. At startup the client moves loose Interface\GlueXML and
// Interface\FrameXML folders aside (renaming them to "<name>.old"), and it checks each interface file against its
// "<file>.sig" signature, treating a missing, short or mismatched signature as a modified file. Each check below is
// made to take its "fine" path; the signature check returns 3, its result for a valid file.
Patch InterfaceEditPatch()
{
    constexpr uint32_t SignatureInvalid = 1;
    constexpr uint32_t SignatureValid = 3;
    return Patch
    {
        "InterfaceEdit",
        {
            Swap("KeepLooseInterfaceFolders",
                Hex("04 85 C0") + Op(JeShort) + Hex("39 56"),
                Hex("04 85 C0") + Op(JmpShort) + Hex("39 56")),
            Swap("SignatureFileMissing",
                Hex("C0 FF 85 C0") + Op(JneShort) + Hex("05 5E 8B"),
                Hex("C0 FF 85 C0") + Op(JmpShort) + Hex("05 5E 8B")),
            Swap("SignatureWrongSize",
                Hex("B6 C0 FF") + MovEax(SignatureInvalid),
                Hex("B6 C0 FF") + MovEax(SignatureValid)),
            Swap("SignatureUnreadable",
                Hex("C0 FF 5F") + MovEax(SignatureInvalid),
                Hex("C0 FF 5F") + MovEax(SignatureValid)),
            Swap("SignatureCompare",
                MovEax(SignatureInvalid) + Op(JgShort) + Hex("12 83 C8 FF F7"),
                MovEax(SignatureInvalid) + Op(JmpShort) + Hex("12 83 C8 FF F7")),
            // "add eax, 3; pop esi; mov esp, ebp; pop ebp; ret" becomes "mov eax, 3" and a jump back to the
            // function's other "pop esi; ..." epilogue just above.
            Swap("SignatureResult",
                Hex("C0 5F 83 C0 03 5E 8B E5 5D C3 CC"),
                Hex("C0 5F") + MovEax(SignatureValid) + Op(JmpShort) + Hex("ED C3 CC")),
        },
        [](const PatchConfig&) {},
    };
}

// The idle check marks you AFK after 5 minutes without input (and logs you out after 30). It branches past both
// when the time since the last input is under 5 minutes; the branch is made unconditional.
Patch NoAutoAfkPatch()
{
    constexpr int32_t AfkAfterMs = 5 * 60 * 1000;
    constexpr int32_t ToSkipIdleActions = -0x213;   // the branch target, from the end of the 6-byte jl
    return Patch
    {
        "NoAutoAfk",
        {
            Swap("AlwaysSkipIdleActions",
                Hex("2B 05") + U32(Client::LastHardwareAction) + Hex("8D 88") + U32(static_cast<uint32_t>(-AfkAfterMs))
                    + Hex("85 C9") + Hex("0F 8C") + U32(static_cast<uint32_t>(ToSkipIdleActions)),
                Hex("2B 05") + U32(Client::LastHardwareAction) + Hex("8D 88") + U32(static_cast<uint32_t>(-AfkAfterMs))
                    + Hex("85 C9") + Hex("E9") + U32(static_cast<uint32_t>(ToSkipIdleActions + 1)) + Op(Nop)),
        },
        [](const PatchConfig&) {},
    };
}

// After asking the server for the mail list, the client waits 60 seconds before it will ask again.
Patch NoMailDelayPatch()
{
    constexpr uint32_t MailListWaitMs = 60000;
    constexpr uint32_t NoWaitMs = 1;
    constexpr uint32_t GetMailListOpcode = 0x23A;   // CMSG_GET_MAIL_LIST
    return Patch
    {
        "NoMailDelay",
        {
            Swap("MailListWait",
                Op(0x05) + U32(MailListWaitMs) + Op(0x68) + U32(GetMailListOpcode),
                Op(0x05) + U32(NoWaitMs) + Op(0x68) + U32(GetMailListOpcode)),
        },
        [](const PatchConfig&) {},
    };
}

// The camera fades the player's model as it zooms in by storing an alpha byte on the model; the store is removed.
Patch NoZoomFadePatch()
{
    constexpr uint32_t ModelAlpha = 0xCB;
    return Patch
    {
        "NoZoomFade",
        {
            Swap("StoreModelAlpha",
                Hex("8A 55 FC") + Hex("88 96") + U32(ModelAlpha) + Hex("8B B6 5C"),
                Hex("8A 55 FC") + Bytes(6, Nop) + Hex("8B B6 5C")),
        },
        [](const PatchConfig&) {},
    };
}

namespace
{
    int g_nameplateDistance = 0;
}

// Nameplates show for units within a distance the client compares as a square (41 yards stock). The float sits right
// after two others that never change (a small epsilon and 400, the 20-yard square), which find it.
Patch NameplateDistancePatch()
{
    return Patch
    {
        "NameplateDistance",
        {
            Code("SquaredDistance",
                Hex("2E 74 01 37") + F32(20.0f * 20.0f),
                WriteBytes(8, [] { return F32(static_cast<float>(g_nameplateDistance * g_nameplateDistance)); })),
        },
        [](const PatchConfig& config) { g_nameplateDistance = config.nameplateDistance; },
    };
}
