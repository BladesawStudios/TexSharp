using System.Buffers.Binary;

namespace TexSharp;

/// <summary>Reads bits from a 16-byte block, least significant bit of the first byte first.</summary>
internal struct BitReader
{
    private readonly ulong _low;
    private readonly ulong _high;
    private int _position;

    public BitReader(ReadOnlySpan<byte> block)
    {
        _low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        _high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
    }

    public int Position => _position;

    public void Skip(int count) => _position += count;

    /// <summary>Reads up to 32 bits. Reading past the end of the block gives zeros.</summary>
    public uint Read(int count)
    {
        if (count == 0) return 0;

        ulong value;
        if (_position >= 128) value = 0;
        else if (_position >= 64) value = _high >> (_position - 64);
        else
        {
            value = _low >> _position;
            if (_position + count > 64 && _position > 0) value |= _high << (64 - _position);
        }

        _position += count;
        return (uint)(value & ((1UL << count) - 1));
    }
}
