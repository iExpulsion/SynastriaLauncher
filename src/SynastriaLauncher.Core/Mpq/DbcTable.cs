using System.Buffers.Binary;

namespace SynastriaLauncher.Core.Mpq;

// A client database (WDBC) table whose fields are all 4 bytes wide. Rows are copies of the record values; the string
// block is kept as it is, since renumbering never touches strings.
public sealed class DbcTable
{
    private const uint Magic = 0x43424457;

    public int FieldCount { get; }
    public List<uint[]> Rows { get; }
    public byte[] Strings { get; }

    public DbcTable(int fieldCount, List<uint[]> rows, byte[] strings)
    {
        FieldCount = fieldCount;
        Rows = rows;
        Strings = strings;
    }

    public static DbcTable Read(byte[] data)
    {
        var header = data.Length >= 20 ? data.AsSpan(0, 20) : new byte[20];
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var fields = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var stringSize = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var valid = BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic && fields > 0 && size == fields * 4 && count >= 0
            && stringSize >= 0 && data.Length >= 20 + (long)count * size + stringSize;
        return valid
            ? new DbcTable(fields,
                Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(data.AsSpan(20 + i * size, size)).ToArray()).ToList(),
                data.AsSpan(20 + count * size, stringSize).ToArray())
            : throw new InvalidDataException("Not a client database table with 4-byte fields.");
    }

    // One field of every row, read straight from the table's bytes (for big tables where only a few fields matter).
    public static IEnumerable<uint> Column(byte[] data, int field)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        return field * 4 < size && data.Length >= 20 + (long)count * size
            ? Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20 + i * size + field * 4)))
            : throw new InvalidDataException("The table is shorter than its header says.");
    }

    public byte[] Write()
    {
        var size = FieldCount * 4;
        var data = new byte[20 + Rows.Count * size + Strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), Rows.Count);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), FieldCount);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), size);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), Strings.Length);
        for (var i = 0; i < Rows.Count; i++)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(Rows[i].AsSpan()).CopyTo(data.AsSpan(20 + i * size));
        }
        Strings.CopyTo(data, 20 + Rows.Count * size);
        return data;
    }
}
