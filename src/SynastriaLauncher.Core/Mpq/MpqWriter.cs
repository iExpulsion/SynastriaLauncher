using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace SynastriaLauncher.Core.Mpq;

// Writes a format 1 MPQ with zlib-compressed sectors and a (listfile), enough for a client patch archive.
public static class MpqWriter
{
    private const int SectorShift = 3;
    private const int SectorSize = 512 << SectorShift;
    private const uint FlagsCompressed = 0x80000200;
    private const int HeaderSize = 32;

    public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var all = files.Append((Name: "(listfile)", Data: Encoding.ASCII.GetBytes(string.Concat(files.Select(f => f.Name + "\r\n"))))).ToList();
        var hashSize = 16;
        while (hashSize < all.Count * 2)
        {
            hashSize *= 2;
        }
        using var body = new MemoryStream();
        var blocks = new uint[all.Count * 4];
        for (var i = 0; i < all.Count; i++)
        {
            var packed = Pack(all[i].Data);
            blocks[i * 4] = (uint)(HeaderSize + body.Length);
            blocks[i * 4 + 1] = (uint)packed.Length;
            blocks[i * 4 + 2] = (uint)all[i].Data.Length;
            blocks[i * 4 + 3] = FlagsCompressed;
            body.Write(packed);
        }
        var hashes = Enumerable.Repeat(0xFFFFFFFFu, hashSize * 4).ToArray();
        for (var i = 0; i < all.Count; i++)
        {
            var slot = (int)(MpqCrypto.Hash(all[i].Name, MpqCrypto.TableOffset) % (uint)hashSize);
            while (hashes[slot * 4 + 3] != 0xFFFFFFFF)
            {
                slot = (slot + 1) % hashSize;
            }
            hashes[slot * 4] = MpqCrypto.Hash(all[i].Name, MpqCrypto.NameA);
            hashes[slot * 4 + 1] = MpqCrypto.Hash(all[i].Name, MpqCrypto.NameB);
            hashes[slot * 4 + 2] = 0;
            hashes[slot * 4 + 3] = (uint)i;
        }
        MpqCrypto.Encrypt(hashes, MpqCrypto.Hash("(hash table)", MpqCrypto.FileKey));
        MpqCrypto.Encrypt(blocks, MpqCrypto.Hash("(block table)", MpqCrypto.FileKey));
        var hashPos = HeaderSize + body.Length;
        var blockPos = hashPos + hashes.Length * 4;
        var size = blockPos + blocks.Length * 4;
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x1A51504D);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), SectorShift);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)hashPos);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)blockPos);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)hashSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)all.Count);
        using var output = new MemoryStream((int)size);
        output.Write(header);
        body.Position = 0;
        body.CopyTo(output);
        output.Write(MemoryMarshal.AsBytes(hashes.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(blocks.AsSpan()));
        return output.ToArray();
    }

    // A sector offset table, then each sector zlib-compressed (method byte 2) or stored when that isn't smaller.
    private static byte[] Pack(byte[] data)
    {
        var count = (data.Length + SectorSize - 1) / SectorSize;
        var sectors = new List<byte[]>(count);
        for (var s = 0; s < count; s++)
        {
            var raw = data.AsSpan(s * SectorSize, Math.Min(SectorSize, data.Length - s * SectorSize));
            using var packed = new MemoryStream();
            packed.WriteByte(0x02);
            using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }
            sectors.Add(packed.Length < raw.Length ? packed.ToArray() : raw.ToArray());
        }
        var offsets = new uint[count + 1];
        offsets[0] = (uint)(offsets.Length * 4);
        for (var s = 0; s < count; s++)
        {
            offsets[s + 1] = offsets[s] + (uint)sectors[s].Length;
        }
        using var result = new MemoryStream();
        result.Write(MemoryMarshal.AsBytes(offsets.AsSpan()));
        foreach (var sector in sectors)
        {
            result.Write(sector);
        }
        return result.ToArray();
    }
}
