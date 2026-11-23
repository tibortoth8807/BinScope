using System.Buffers.Binary;
using System.Text;

namespace BinScope.Core.Binary;

/// <summary>Thrown when a file does not follow its format. Carries the byte offset of the problem.</summary>
public sealed class BinaryFormatException(string message, int offset) : Exception($"{message} (at byte {offset})")
{
    public int Offset { get; } = offset;
}

/// <summary>Little-endian writer with back-patching (for section tables).</summary>
public sealed class ByteWriter
{
    private readonly MemoryStream _stream = new();

    public int Length => (int)_stream.Length;

    public ByteWriter U8(int value)
    {
        _stream.WriteByte((byte)value);
        return this;
    }

    public ByteWriter U16(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
        _stream.Write(b);
        return this;
    }

    public ByteWriter U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        _stream.Write(b);
        return this;
    }

    public ByteWriter I32(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        _stream.Write(b);
        return this;
    }

    public ByteWriter F64(double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, value);
        _stream.Write(b);
        return this;
    }

    public ByteWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        return this;
    }

    /// <summary>UTF-8 string with a u16 length prefix.</summary>
    public ByteWriter Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length > ushort.MaxValue) throw new ArgumentException("String too long for a u16 length", nameof(s));
        return U16(b.Length).Bytes(b);
    }

    /// <summary>UTF-8 string with a u32 length prefix (block sources).</summary>
    public ByteWriter LongStr(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return U32((uint)b.Length).Bytes(b);
    }

    /// <summary>Overwrites a u32 at an earlier position.</summary>
    public void PatchU32(int at, uint value)
    {
        var buffer = _stream.GetBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at, 4), value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>Little-endian reader bounded by <see cref="End"/>; reading past it throws <see cref="BinaryFormatException"/>.</summary>
public sealed class ByteReader(byte[] bytes, int? end = null)
{
    public byte[] Data { get; } = bytes;
    public int End { get; } = end ?? bytes.Length;
    public int Pos { get; set; }

    private void Need(int n)
    {
        if (Pos + n > End) throw new BinaryFormatException($"Unexpected end of data, {n} more byte(s) needed", Pos);
    }

    public int U8()
    {
        Need(1);
        return Data[Pos++];
    }

    public int U16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Pos, 2));
        Pos += 2;
        return v;
    }

    public uint U32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos, 4));
        Pos += 4;
        return v;
    }

    public int I32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(Pos, 4));
        Pos += 4;
        return v;
    }

    public double F64()
    {
        Need(8);
        var v = BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(Pos, 8));
        Pos += 8;
        return v;
    }

    public byte[] Take(int n)
    {
        Need(n);
        var v = Data.AsSpan(Pos, n).ToArray();
        Pos += n;
        return v;
    }

    public string Str() => Encoding.UTF8.GetString(Take(U16()));

    public string LongStr()
    {
        var n = U32();
        if (n > int.MaxValue) throw new BinaryFormatException("String length out of range", Pos - 4);
        return Encoding.UTF8.GetString(Take((int)n));
    }
}

/// <summary>CRC-32 (IEEE 802.3), as used by zip and PNG.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

public static class Hex
{
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string U32(uint value) => value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
}
