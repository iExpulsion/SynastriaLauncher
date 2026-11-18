namespace SynastriaLauncher.Core.Patching;

// Code patches live in their own section appended after the exe's last one. The exe has no relocations, so the
// routines are assembled for the section's fixed address (image base 0x400000 + Rva).
internal static class PatchSection
{
    public const string Name = ".synpat";
    public const uint Rva = 0x9FE000;
    public const uint Size = 0x14000;

    public static PeSection? Find(byte[] bytes) =>
        PeLayout.Read(bytes)?.Sections.FirstOrDefault(s => s.Name == Name && s.Rva == Rva && s.RawSize >= Size);

    public static (byte[]? Bytes, string Problem) Add(byte[] bytes)
    {
        var layout = PeLayout.Read(bytes);
        (byte[]? Bytes, string Problem) result;
        if (Find(bytes) is not null)
        {
            result = (bytes, "");
        }
        else if (layout is null)
        {
            result = (null, "No valid PE header found");
        }
        else if (layout.Sections.Any(s => s.Name == Name))
        {
            result = (null, "The exe has an older, smaller patch section; restore the original and apply again");
        }
        else if (layout.SizeOfImage != Rva || bytes.Length % 0x200 != 0)
        {
            result = (null, "The exe doesn't end where the patch section would go");
        }
        else if (layout.Table + 40 * (layout.Count + 1) > layout.SizeOfHeaders)
        {
            result = (null, "There is no room in the header for another section");
        }
        else
        {
            var work = new byte[bytes.Length + Size];
            bytes.CopyTo(work, 0);
            var header = layout.Table + 40 * layout.Count;
            Bytes.Latin1.GetBytes(Name.PadRight(8, '\0')).CopyTo(work, header);
            Bytes.WriteUInt32(work, header + 8, Size);
            Bytes.WriteUInt32(work, header + 12, Rva);
            Bytes.WriteUInt32(work, header + 16, Size);
            Bytes.WriteUInt32(work, header + 20, (uint)bytes.Length);
            Bytes.WriteUInt32(work, header + 36, 0xE0000020);
            Bytes.WriteUInt16(work, layout.PeOffset + 6, (ushort)(layout.Count + 1));
            Bytes.WriteUInt32(work, layout.Optional + 56, Rva + Size);
            result = (work, "");
        }
        return result;
    }
}
