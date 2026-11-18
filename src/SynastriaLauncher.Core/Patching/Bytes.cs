using System.Text;

namespace SynastriaLauncher.Core.Patching;

internal static class Bytes
{
    public static readonly Encoding Latin1 = Encoding.Latin1;

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    public static List<int> FindAll(byte[] haystack, byte[] needle)
    {
        var offsets = new List<int>();
        var start = 0;
        var found = haystack.AsSpan().IndexOf(needle);
        while (found >= 0)
        {
            offsets.Add(start + found);
            start += found + 1;
            found = haystack.AsSpan(start).IndexOf(needle);
        }
        return offsets;
    }

    public static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    // True when the region holds expected followed only by zeros up to length.
    public static bool RegionMatches(byte[] bytes, int offset, int length, ReadOnlySpan<byte> expected)
    {
        var region = bytes.AsSpan(offset, length);
        return region[..expected.Length].SequenceEqual(expected) && !region[expected.Length..].ContainsAnyExcept((byte)0);
    }

    public static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

    public static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

    public static void WriteSingle(byte[] bytes, int offset, float value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
}
