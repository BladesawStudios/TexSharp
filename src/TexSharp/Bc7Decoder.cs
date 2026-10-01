using System.Numerics;

namespace TexSharp;

// BC7 (BPTC): eight modes, up to three subsets per block, endpoints and indices packed into 128 bits.
internal static class Bc7Decoder
{
    // One bit (two subsets) or two bits (three subsets) per pixel, pixel 0 in the lowest bits.
    internal static readonly ushort[] Partitions2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
        0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
        0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
        0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
        0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    private static readonly uint[] Partitions3 =
    [
        0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
        0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090, 0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250,
        0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
        0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414, 0x50A4A450, 0x6A5A0200,
        0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424, 0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50,
        0x500AA550, 0xAAAA4444, 0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
        0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580, 0xAA141414, 0x96960000,
        0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000, 0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
    ];

    // The pixel whose index loses its top bit, for the second subset of each two-subset partition...
    internal static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    // ...and for the second and third subsets of each three-subset partition.
    private static readonly byte[] Anchor3A =
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];

    private static readonly byte[] Anchor3B =
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    internal static readonly byte[][] Weights =
    [
        [], [],
        [0, 21, 43, 64],
        [0, 9, 18, 27, 37, 46, 55, 64],
        [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64],
    ];

    private readonly record struct Mode(int Subsets, int PartitionBits, int RotationBits, bool IndexSelection,
        int ColorBits, int AlphaBits, bool EndpointPBits, bool SharedPBits, int IndexBits, int AlphaIndexBits);

    private static readonly Mode[] Modes =
    [
        new(3, 4, 0, false, 4, 0, true, false, 3, 0),
        new(2, 6, 0, false, 6, 0, false, true, 3, 0),
        new(3, 6, 0, false, 5, 0, false, false, 2, 0),
        new(2, 6, 0, false, 7, 0, true, false, 2, 0),
        new(1, 0, 2, true, 5, 6, false, false, 2, 3),
        new(1, 0, 2, false, 7, 8, false, false, 2, 2),
        new(1, 0, 0, false, 7, 7, true, false, 4, 0),
        new(2, 6, 0, false, 5, 5, true, false, 2, 0),
    ];

    /// <summary>Decodes one 16-byte block into 4x4 RGBA pixels (64 bytes, row-major). A reserved mode gives all zeros.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        uint first = block[0];
        if (first == 0)
        {
            pixels[..64].Clear();
            return;
        }

        var bits = new BitReader(block);
        int modeIndex = BitOperations.TrailingZeroCount(first);
        bits.Skip(modeIndex + 1);
        Mode mode = Modes[modeIndex];

        int partition = (int)bits.Read(mode.PartitionBits);
        int rotation = (int)bits.Read(mode.RotationBits);
        bool swapIndices = mode.IndexSelection && bits.Read(1) == 1;

        int subsets = mode.Subsets;
        Span<int> endpoints = stackalloc int[24]; // [subset][end][channel]
        for (int channel = 0; channel < 3; channel++)
            for (int i = 0; i < subsets * 2; i++)
                endpoints[i * 4 + channel] = (int)bits.Read(mode.ColorBits);
        if (mode.AlphaBits > 0)
            for (int i = 0; i < subsets * 2; i++)
                endpoints[i * 4 + 3] = (int)bits.Read(mode.AlphaBits);

        Span<int> pbits = stackalloc int[6];
        if (mode.EndpointPBits)
            for (int i = 0; i < subsets * 2; i++) pbits[i] = (int)bits.Read(1);
        else if (mode.SharedPBits)
            for (int s = 0; s < subsets; s++) pbits[s * 2] = pbits[s * 2 + 1] = (int)bits.Read(1);

        bool hasP = mode.EndpointPBits || mode.SharedPBits;
        int colorBits = mode.ColorBits + (hasP ? 1 : 0);
        int alphaBits = mode.AlphaBits + (hasP ? 1 : 0);
        for (int i = 0; i < subsets * 2; i++)
        {
            for (int channel = 0; channel < 3; channel++)
                endpoints[i * 4 + channel] = Expand(hasP ? (endpoints[i * 4 + channel] << 1) | pbits[i] : endpoints[i * 4 + channel], colorBits);
            endpoints[i * 4 + 3] = mode.AlphaBits > 0 ? Expand(hasP ? (endpoints[i * 4 + 3] << 1) | pbits[i] : endpoints[i * 4 + 3], alphaBits) : 255;
        }

        Span<byte> subsetOf = stackalloc byte[16];
        Span<int> anchors = stackalloc int[3];
        for (int i = 0; i < 16; i++)
        {
            subsetOf[i] = subsets switch
            {
                2 => (byte)((Partitions2[partition] >> i) & 1),
                3 => (byte)((Partitions3[partition] >> (2 * i)) & 3),
                _ => 0,
            };
        }
        if (subsets == 2) anchors[1] = Anchor2[partition];
        if (subsets == 3)
        {
            anchors[1] = Anchor3A[partition];
            anchors[2] = Anchor3B[partition];
        }

        Span<byte> indices = stackalloc byte[16];
        Span<byte> alphaIndices = stackalloc byte[16];
        ReadIndices(ref bits, indices, subsetOf, anchors, mode.IndexBits);
        bool separateAlpha = mode.AlphaIndexBits > 0;
        if (separateAlpha) ReadIndices(ref bits, alphaIndices, subsetOf, anchors, mode.AlphaIndexBits);

        for (int i = 0; i < 16; i++)
        {
            int s = subsetOf[i] * 2 * 4;
            int colorIndexBits = mode.IndexBits, alphaIndexBits = separateAlpha ? mode.AlphaIndexBits : mode.IndexBits;
            int colorIndex = indices[i], alphaIndex = separateAlpha ? alphaIndices[i] : indices[i];
            if (swapIndices)
            {
                (colorIndexBits, alphaIndexBits) = (alphaIndexBits, colorIndexBits);
                (colorIndex, alphaIndex) = (alphaIndex, colorIndex);
            }

            int cw = Weights[colorIndexBits][colorIndex], aw = Weights[alphaIndexBits][alphaIndex];
            int r = Lerp(endpoints[s], endpoints[s + 4], cw);
            int g = Lerp(endpoints[s + 1], endpoints[s + 5], cw);
            int b = Lerp(endpoints[s + 2], endpoints[s + 6], cw);
            int a = Lerp(endpoints[s + 3], endpoints[s + 7], aw);

            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }

            pixels[i * 4] = (byte)r;
            pixels[i * 4 + 1] = (byte)g;
            pixels[i * 4 + 2] = (byte)b;
            pixels[i * 4 + 3] = (byte)a;
        }
    }

    private static void ReadIndices(ref BitReader bits, Span<byte> indices, ReadOnlySpan<byte> subsetOf, ReadOnlySpan<int> anchors, int indexBits)
    {
        for (int i = 0; i < 16; i++)
            indices[i] = (byte)bits.Read(anchors[subsetOf[i]] == i ? indexBits - 1 : indexBits);
    }

    private static int Expand(int value, int bits) => (value << (8 - bits)) | (value >> (2 * bits - 8));

    private static int Lerp(int a, int b, int weight) => (a * (64 - weight) + b * weight + 32) >> 6;
}
