using System.Buffers.Binary;

namespace TexSharp;

// BC1 to BC5 (DXT1, DXT3, DXT5, ATI1, ATI2). Every Decode* method fills 4x4 RGBA pixels: 64 bytes, row-major.
internal static class BcDecoder
{
    /// <summary>
    /// BC1. With <paramref name="fourColor"/> set (BC2 and BC3 colour blocks) the block is always read in
    /// four-colour mode; otherwise a block whose first colour is not greater than its second has one
    /// transparent black entry.
    /// </summary>
    public static void DecodeBc1(ReadOnlySpan<byte> block, Span<byte> pixels, bool fourColor = false)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);

        Span<byte> palette = stackalloc byte[16];
        Expand565(c0, palette[..4]);
        Expand565(c1, palette[4..8]);
        if (fourColor || c0 > c1)
        {
            for (int c = 0; c < 3; c++)
            {
                palette[8 + c] = (byte)((2 * palette[c] + palette[4 + c]) / 3);
                palette[12 + c] = (byte)((palette[c] + 2 * palette[4 + c]) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (int c = 0; c < 3; c++)
                palette[8 + c] = (byte)((palette[c] + palette[4 + c]) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = palette[15] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int i = 0; i < 16; i++)
            palette.Slice((int)((indices >> (2 * i)) & 3) * 4, 4).CopyTo(pixels[(i * 4)..]);
    }

    /// <summary>BC2: four-bit alpha beside a BC1 colour block.</summary>
    public static void DecodeBc2(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        DecodeBc1(block[8..], pixels, fourColor: true);
        for (int i = 0; i < 16; i++)
        {
            int nibble = (block[i >> 1] >> ((i & 1) * 4)) & 0xF;
            pixels[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    /// <summary>BC3: an eight-bit interpolated alpha block beside a BC1 colour block.</summary>
    public static void DecodeBc3(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        DecodeBc1(block[8..], pixels, fourColor: true);
        Span<byte> alpha = stackalloc byte[16];
        DecodeChannel(block, alpha, signed: false);
        for (int i = 0; i < 16; i++) pixels[i * 4 + 3] = alpha[i];
    }

    /// <summary>BC4: one channel, shown as grey.</summary>
    public static void DecodeBc4(ReadOnlySpan<byte> block, Span<byte> pixels, bool signed)
    {
        Span<byte> channel = stackalloc byte[16];
        DecodeChannel(block, channel, signed);
        for (int i = 0; i < 16; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = channel[i];
            pixels[i * 4 + 3] = 255;
        }
    }

    /// <summary>BC5: two channels as red and green, blue zero.</summary>
    public static void DecodeBc5(ReadOnlySpan<byte> block, Span<byte> pixels, bool signed)
    {
        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];
        DecodeChannel(block, red, signed);
        DecodeChannel(block[8..], green, signed);
        for (int i = 0; i < 16; i++)
        {
            pixels[i * 4] = red[i];
            pixels[i * 4 + 1] = green[i];
            pixels[i * 4 + 2] = 0;
            pixels[i * 4 + 3] = 255;
        }
    }

    // One 8-byte BC4 block as 16 values. Signed blocks are remapped from -1..1 to 0..255.
    private static void DecodeChannel(ReadOnlySpan<byte> block, Span<byte> values, bool signed)
    {
        Span<int> palette = stackalloc int[8];
        if (signed)
        {
            int e0 = Math.Max((sbyte)block[0], (sbyte)-127), e1 = Math.Max((sbyte)block[1], (sbyte)-127);
            palette[0] = e0;
            palette[1] = e1;
            if (e0 > e1)
                for (int i = 2; i < 8; i++) palette[i] = DivRound((8 - i) * e0 + (i - 1) * e1, 7);
            else
            {
                for (int i = 2; i < 6; i++) palette[i] = DivRound((6 - i) * e0 + (i - 1) * e1, 5);
                palette[6] = -127;
                palette[7] = 127;
            }
            for (int i = 0; i < 8; i++) palette[i] = (palette[i] + 127) * 255 / 254;
        }
        else
        {
            int e0 = block[0], e1 = block[1];
            palette[0] = e0;
            palette[1] = e1;
            if (e0 > e1)
                for (int i = 2; i < 8; i++) palette[i] = ((8 - i) * e0 + (i - 1) * e1) / 7;
            else
            {
                for (int i = 2; i < 6; i++) palette[i] = ((6 - i) * e0 + (i - 1) * e1) / 5;
                palette[6] = 0;
                palette[7] = 255;
            }
        }

        ulong indices = BinaryPrimitives.ReadUInt64LittleEndian(block) >> 16;
        for (int i = 0; i < 16; i++)
            values[i] = (byte)palette[(int)((indices >> (3 * i)) & 7)];
    }

    private static int DivRound(int numerator, int denominator)
        => numerator >= 0 ? (numerator + denominator / 2) / denominator : -((-numerator + denominator / 2) / denominator);

    private static void Expand565(ushort color, Span<byte> rgba)
    {
        int r = color >> 11, g = (color >> 5) & 0x3F, b = color & 0x1F;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }
}
