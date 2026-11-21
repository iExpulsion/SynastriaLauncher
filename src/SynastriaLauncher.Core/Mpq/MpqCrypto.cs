namespace SynastriaLauncher.Core.Mpq;

// The MPQ format's string hashes and table encryption.
internal static class MpqCrypto
{
    public const uint TableOffset = 0, NameA = 1, NameB = 2, FileKey = 3;

    private static readonly uint[] Table = BuildTable();

    public static uint Hash(string text, uint type)
    {
        uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
        foreach (var c in text.ToUpperInvariant().Replace('/', '\\'))
        {
            seed1 = Table[type * 0x100 + c] ^ (seed1 + seed2);
            seed2 = c + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    public static void Decrypt(Span<uint> data, uint key)
    {
        var seed = 0xEEEEEEEEu;
        for (var i = 0; i < data.Length; i++)
        {
            seed += Table[0x400 + (key & 0xFF)];
            var value = data[i] ^ (key + seed);
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = value + seed + (seed << 5) + 3;
            data[i] = value;
        }
    }

    public static void Encrypt(Span<uint> data, uint key)
    {
        var seed = 0xEEEEEEEEu;
        for (var i = 0; i < data.Length; i++)
        {
            seed += Table[0x400 + (key & 0xFF)];
            var value = data[i];
            data[i] = value ^ (key + seed);
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = value + seed + (seed << 5) + 3;
        }
    }

    private static uint[] BuildTable()
    {
        var table = new uint[0x500];
        var seed = 0x00100001u;
        for (var i = 0; i < 0x100; i++)
        {
            for (var j = 0; j < 5; j++)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var high = (seed & 0xFFFF) << 0x10;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[i + j * 0x100] = high | (seed & 0xFFFF);
            }
        }
        return table;
    }
}
