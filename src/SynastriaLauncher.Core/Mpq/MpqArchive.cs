using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace SynastriaLauncher.Core.Mpq;

// Reads files out of a 3.3.5 client's MPQ archives (format 1 and 2 headers, zlib or stored sectors, encrypted files).
public sealed class MpqArchive : IDisposable
{
    private const uint HeaderMagic = 0x1A51504D;
    private const uint UserDataMagic = 0x1B51504D;
    private const uint FlagImplode = 0x100;
    private const uint FlagCompress = 0x200;
    private const uint FlagEncrypted = 0x10000;
    private const uint FlagFixKey = 0x20000;
    private const uint FlagSingleUnit = 0x1000000;
    private const uint FlagSectorCrc = 0x4000000;
    private const uint FlagExists = 0x80000000;
    private const uint NoBlock = 0xFFFFFFFF;
    private const uint DeletedBlock = 0xFFFFFFFE;

    private readonly FileStream _stream;
    private readonly long _start;
    private readonly int _sectorSize;
    private readonly uint[] _hashTable;
    private readonly uint[] _blockTable;

    public MpqArchive(string path)
    {
        _stream = File.OpenRead(path);
        try
        {
            var header = ReadAt(0, 32);
            _start = BinaryPrimitives.ReadUInt32LittleEndian(header) == UserDataMagic ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) : 0;
            header = _start == 0 ? header : ReadAt(_start, 32);
            _sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(header) == HeaderMagic
                ? 512 << BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14))
                : throw new InvalidDataException($"{Path.GetFileName(path)} isn't an MPQ archive.");
            _hashTable = ReadTable(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)),
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)) * 4, "(hash table)");
            _blockTable = ReadTable(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)),
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28)) * 4, "(block table)");
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public bool Contains(string name) => FindBlock(name) >= 0;

    public byte[]? Read(string name)
    {
        var block = FindBlock(name);
        byte[]? data = null;
        if (block >= 0)
        {
            var offset = _blockTable[block * 4];
            var packedSize = (int)_blockTable[block * 4 + 1];
            var size = (int)_blockTable[block * 4 + 2];
            var flags = _blockTable[block * 4 + 3];
            var key = (flags & FlagEncrypted) == 0 ? 0u : MpqCrypto.Hash(name[(name.LastIndexOf('\\') + 1)..], MpqCrypto.FileKey);
            key = (flags & FlagFixKey) == 0 ? key : (key + offset) ^ (uint)size;
            var raw = ReadAt(_start + offset, packedSize);
            data = (flags & FlagSingleUnit) != 0 ? Decode(raw, size, flags, key) : ReadSectors(raw, size, flags, key);
        }
        return data;
    }

    private byte[] ReadSectors(byte[] raw, int size, uint flags, uint key)
    {
        var output = new byte[size];
        var count = (size + _sectorSize - 1) / _sectorSize;
        var packed = (flags & (FlagCompress | FlagImplode)) != 0;
        var offsets = new uint[count + 1];
        if (packed)
        {
            var table = new uint[count + 1 + ((flags & FlagSectorCrc) != 0 ? 1 : 0)];
            MemoryMarshal.Cast<byte, uint>(raw.AsSpan(0, table.Length * 4)).CopyTo(table);
            if ((flags & FlagEncrypted) != 0)
            {
                MpqCrypto.Decrypt(table, key - 1);
            }
            table.AsSpan(0, count + 1).CopyTo(offsets);
        }
        else
        {
            for (var s = 0; s <= count; s++)
            {
                offsets[s] = (uint)Math.Min(s * _sectorSize, size);
            }
        }
        for (var s = 0; s < count; s++)
        {
            var length = Math.Min(_sectorSize, size - s * _sectorSize);
            var sector = raw.AsSpan((int)offsets[s], (int)(offsets[s + 1] - offsets[s])).ToArray();
            Decode(sector, length, flags, key + (uint)s).CopyTo(output, s * _sectorSize);
        }
        return output;
    }

    private static byte[] Decode(byte[] sector, int size, uint flags, uint key)
    {
        if ((flags & FlagEncrypted) != 0)
        {
            MpqCrypto.Decrypt(MemoryMarshal.Cast<byte, uint>(sector.AsSpan(0, sector.Length / 4 * 4)), key);
        }
        byte[] result;
        if (sector.Length >= size)
        {
            result = sector.Length == size ? sector : sector[..size];
        }
        else if ((flags & FlagImplode) == 0 && sector[0] == 0x02)
        {
            using var zlib = new ZLibStream(new MemoryStream(sector, 1, sector.Length - 1), CompressionMode.Decompress);
            result = new byte[size];
            zlib.ReadExactly(result);
        }
        else
        {
            throw new InvalidDataException($"MPQ compression 0x{sector[0]:X2} isn't supported.");
        }
        return result;
    }

    private int FindBlock(string name)
    {
        var entries = _hashTable.Length / 4;
        var start = (int)(MpqCrypto.Hash(name, MpqCrypto.TableOffset) % (uint)entries);
        var a = MpqCrypto.Hash(name, MpqCrypto.NameA);
        var b = MpqCrypto.Hash(name, MpqCrypto.NameB);
        for (var probe = 0; probe < entries; probe++)
        {
            var i = (start + probe) % entries;
            var block = _hashTable[i * 4 + 3];
            if (block == NoBlock)
            {
                break;
            }
            if (_hashTable[i * 4] == a && _hashTable[i * 4 + 1] == b && block != DeletedBlock && (_blockTable[block * 4 + 3] & FlagExists) != 0)
            {
                return (int)block;
            }
        }
        return -1;
    }

    private uint[] ReadTable(uint position, int count, string keyName)
    {
        var table = MemoryMarshal.Cast<byte, uint>(ReadAt(_start + position, count * 4)).ToArray();
        MpqCrypto.Decrypt(table, MpqCrypto.Hash(keyName, MpqCrypto.FileKey));
        return table;
    }

    private byte[] ReadAt(long position, int count)
    {
        var buffer = new byte[count];
        _stream.Position = position;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose() => _stream.Dispose();
}
