namespace SynastriaLauncher.Core.Patching;

internal sealed record PeSection(string Name, uint Rva, uint RawSize, uint RawOffset, uint VirtualSize);

internal sealed record PeLayout(
    int PeOffset, int Optional, int Table, int Count, uint SizeOfImage, uint SizeOfHeaders,
    IReadOnlyList<PeSection> Sections)
{
    public int EntryPointField => Optional + 16;

    public static PeLayout? Read(byte[] bytes)
    {
        var peOffset = bytes.Length >= 0x40 ? BitConverter.ToInt32(bytes, 0x3C) : 0;
        var isPe = peOffset > 0 && peOffset + 0xF8 <= bytes.Length && BitConverter.ToUInt32(bytes, peOffset) == 0x4550;
        PeLayout? layout = null;
        if (isPe)
        {
            int count = BitConverter.ToUInt16(bytes, peOffset + 6);
            var optional = peOffset + 24;
            var table = optional + BitConverter.ToUInt16(bytes, peOffset + 20);
            var sections = new List<PeSection>(count);
            for (var i = 0; i < count; i++)
            {
                var header = table + 40 * i;
                sections.Add(new PeSection(
                    Bytes.Latin1.GetString(bytes, header, 8).TrimEnd('\0'),
                    BitConverter.ToUInt32(bytes, header + 12),
                    BitConverter.ToUInt32(bytes, header + 16),
                    BitConverter.ToUInt32(bytes, header + 20),
                    BitConverter.ToUInt32(bytes, header + 8)));
            }
            layout = new PeLayout(peOffset, optional, table, count,
                BitConverter.ToUInt32(bytes, optional + 56), BitConverter.ToUInt32(bytes, optional + 60), sections);
        }
        return layout;
    }
}
