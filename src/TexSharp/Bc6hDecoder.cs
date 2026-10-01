namespace TexSharp;

// BC6H: half-float HDR colour in 14 modes. The endpoint fields are scattered through the block, so each
// mode is written below the way the D3D11 spec lists it: fields in stream order, "rw9:0" being bits 9 to 0
// of the red of endpoint 0, least significant bit first, and "rw10:11" the same bits in reverse.
// Endpoints are named w, x, y, z: the start and end of subset 0, then of subset 1.
internal static class Bc6hDecoder
{
    private sealed record ModeInfo(
        int ModeBits, bool TwoSubsets, int EndpointBits, int[] DeltaBits, bool Transformed, (char Field, int Endpoint, int Channel, int High, int Low)[] Fields);

    private static readonly ModeInfo?[] ByModeBits = BuildModes();

    private static ModeInfo?[] BuildModes()
    {
        ModeInfo?[] modes = new ModeInfo?[32];

        void Add(int bits, bool two, int w, int[] delta, bool transformed, string fields)
            => modes[bits] = new ModeInfo(bits, two, w, delta, transformed, Parse(fields));

        Add(0b00000, true, 10, [5, 5, 5], true, "gy4 by4 bz4 rw9:0 gw9:0 bw9:0 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0");
        Add(0b00001, true, 7, [6, 6, 6], true, "gy5 gz4 gz5 rw6:0 bz0 bz1 by4 gw6:0 by5 bz2 gy4 bw6:0 bz3 bz5 bz4 rx5:0 gy3:0 gx5:0 gz3:0 bx5:0 by3:0 ry5:0 rz5:0 d4:0");
        Add(0b00010, true, 11, [5, 4, 4], true, "rw9:0 gw9:0 bw9:0 rx4:0 rw10 gy3:0 gx3:0 gw10 bz0 gz3:0 bx3:0 bw10 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0");
        Add(0b00110, true, 11, [4, 5, 4], true, "rw9:0 gw9:0 bw9:0 rx3:0 rw10 gz4 gy3:0 gx4:0 gw10 gz3:0 bx3:0 bw10 bz1 by3:0 ry3:0 bz0 bz2 rz3:0 gy4 bz3 d4:0");
        Add(0b01010, true, 11, [4, 4, 5], true, "rw9:0 gw9:0 bw9:0 rx3:0 rw10 by4 gy3:0 gx3:0 gw10 bz0 gz3:0 bx4:0 bw10 by3:0 ry3:0 bz1 bz2 rz3:0 bz4 bz3 d4:0");
        Add(0b01110, true, 9, [5, 5, 5], true, "rw8:0 by4 gw8:0 gy4 bw8:0 bz4 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0");
        Add(0b10010, true, 8, [6, 5, 5], true, "rw7:0 gz4 by4 gw7:0 bz2 gy4 bw7:0 bz3 bz4 rx5:0 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry5:0 rz5:0 d4:0");
        Add(0b10110, true, 8, [5, 6, 5], true, "rw7:0 bz0 by4 gw7:0 gy5 gy4 bw7:0 gz5 bz4 rx4:0 gz4 gy3:0 gx5:0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0");
        Add(0b11010, true, 8, [5, 5, 6], true, "rw7:0 bz1 by4 gw7:0 by5 gy4 bw7:0 bz5 bz4 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx5:0 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0");
        Add(0b11110, true, 6, [6, 6, 6], false, "rw5:0 gz4 bz0 bz1 by4 gw5:0 gy5 by5 bz2 gy4 bw5:0 gz5 bz3 bz5 bz4 rx5:0 gy3:0 gx5:0 gz3:0 bx5:0 by3:0 ry5:0 rz5:0 d4:0");
        Add(0b00011, false, 10, [10, 10, 10], false, "rw9:0 gw9:0 bw9:0 rx9:0 gx9:0 bx9:0");
        Add(0b00111, false, 11, [9, 9, 9], true, "rw9:0 gw9:0 bw9:0 rx8:0 rw10 gx8:0 gw10 bx8:0 bw10");
        Add(0b01011, false, 12, [8, 8, 8], true, "rw9:0 gw9:0 bw9:0 rx7:0 rw10:11 gx7:0 gw10:11 bx7:0 bw10:11");
        Add(0b01111, false, 16, [4, 4, 4], true, "rw9:0 gw9:0 bw9:0 rx3:0 rw10:15 gx3:0 gw10:15 bx3:0 bw10:15");
        return modes;
    }

    private static (char Field, int Endpoint, int Channel, int High, int Low)[] Parse(string fields)
    {
        List<(char, int, int, int, int)> list = [];
        foreach (string token in fields.Split(' '))
        {
            bool partition = token[0] == 'd';
            int channel = token[0] switch { 'r' => 0, 'g' => 1, 'b' => 2, _ => -1 };
            int endpoint = partition ? -1 : token[1] switch { 'w' => 0, 'x' => 1, 'y' => 2, _ => 3 };
            string range = token[(partition ? 1 : 2)..];
            int colon = range.IndexOf(':');
            int high = int.Parse(colon < 0 ? range : range[..colon]);
            int low = colon < 0 ? high : int.Parse(range[(colon + 1)..]);
            list.Add((token[0], endpoint, channel, high, low));
        }
        return [.. list];
    }

    /// <summary>
    /// Decodes one 16-byte block into 4x4 RGBA pixels (64 bytes, row-major). HDR values are clamped to 0..1.
    /// A reserved mode gives zeros with full alpha.
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> pixels, bool signed)
    {
        var bits = new BitReader(block);
        int modeBits = (int)bits.Read(2);
        if (modeBits > 1) modeBits |= (int)bits.Read(3) << 2;

        if (ByModeBits[modeBits] is not { } mode)
        {
            for (int i = 0; i < 16; i++)
            {
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = 0;
                pixels[i * 4 + 3] = 255;
            }
            return;
        }

        Span<int> endpoints = stackalloc int[12]; // [endpoint][channel]
        int partition = 0;
        foreach (var (field, endpoint, channel, high, low) in mode.Fields)
        {
            int count = Math.Abs(high - low) + 1;
            uint value = bits.Read(count);
            for (int k = 0; k < count; k++)
            {
                int bit = (int)((value >> k) & 1);
                int position = high >= low ? low + k : low - k;
                if (field == 'd') partition |= bit << position;
                else endpoints[endpoint * 3 + channel] |= bit << position;
            }
        }

        int count2 = mode.TwoSubsets ? 4 : 2;
        int wBits = mode.EndpointBits;
        for (int channel = 0; channel < 3; channel++)
        {
            int deltaBits = mode.DeltaBits[channel];
            if (signed) endpoints[channel] = SignExtend(endpoints[channel], wBits);

            for (int e = 1; e < count2; e++)
            {
                int value = endpoints[e * 3 + channel];
                if (mode.Transformed)
                {
                    value = (endpoints[channel] + SignExtend(value, deltaBits)) & ((1 << wBits) - 1);
                    if (signed) value = SignExtend(value, wBits);
                }
                else if (signed)
                {
                    value = SignExtend(value, wBits);
                }
                endpoints[e * 3 + channel] = value;
            }
        }
        for (int i = 0; i < count2 * 3; i++) endpoints[i] = Unquantize(endpoints[i], wBits, signed);

        int indexBits = mode.TwoSubsets ? 3 : 4;
        byte[] weights = Bc7Decoder.Weights[indexBits];
        int anchor = mode.TwoSubsets ? Bc7Decoder.Anchor2[partition] : -1;
        uint partitionMask = mode.TwoSubsets ? Bc7Decoder.Partitions2[partition] : 0u;

        for (int i = 0; i < 16; i++)
        {
            int subset = (int)((partitionMask >> i) & 1);
            bool isAnchor = i == 0 || (subset == 1 && i == anchor);
            int index = (int)bits.Read(isAnchor ? indexBits - 1 : indexBits);
            int weight = weights[index];

            int e0 = subset * 6, e1 = e0 + 3;
            for (int channel = 0; channel < 3; channel++)
            {
                int value = (endpoints[e0 + channel] * (64 - weight) + endpoints[e1 + channel] * weight + 32) >> 6;
                pixels[i * 4 + channel] = ToByte(FinishUnquantize(value, signed));
            }
            pixels[i * 4 + 3] = 255;
        }
    }

    private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    private static int Unquantize(int value, int bits, bool signed)
    {
        if (!signed)
        {
            if (bits >= 15) return value;
            if (value == 0) return 0;
            if (value == (1 << bits) - 1) return 0xFFFF;
            return ((value << 15) + 0x4000) >> (bits - 1);
        }

        if (bits >= 16) return value;
        bool negative = value < 0;
        if (negative) value = -value;
        int result = value == 0 ? 0 : value >= (1 << (bits - 1)) - 1 ? 0x7FFF : ((value << 15) + 0x4000) >> (bits - 1);
        return negative ? -result : result;
    }

    // Half-float bits from an interpolated, unquantized endpoint value.
    private static ushort FinishUnquantize(int value, bool signed)
    {
        if (!signed) return (ushort)((value * 31) >> 6);
        return value < 0 ? (ushort)((((-value) * 31) >> 5) | 0x8000) : (ushort)((value * 31) >> 5);
    }

    private static byte ToByte(ushort halfBits)
    {
        double f = (double)BitConverter.UInt16BitsToHalf(halfBits);
        if (!(f > 0)) return 0;
        if (f >= 1) return 255;
        return (byte)(f * 255 + 0.5);
    }
}
