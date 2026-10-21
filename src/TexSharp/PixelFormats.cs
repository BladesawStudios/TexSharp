namespace TexSharp;

/// <summary>Conversions between the small uncompressed layouts and RGBA8.</summary>
/// <remarks>
/// Most image editors won't open a one or two channel DDS or a packed 16-bit one, so these are exported as RGBA8 and
/// collapsed back on import. Every channel is 8 bits or fewer and is widened by repeating its bits, so for an unedited
/// image expanding then collapsing gives the original bytes. Rgb10A2 doesn't fit in 8 bits per channel and is left out
/// of the pair. A packed layout is the place and width of each channel in a little-endian 8 or 16-bit value: Rgb565 and
/// Rgba4 as Switch Toolbox reads them, Bgr565 as Rgb565 with red and blue swapped, and the others with their
/// first-named channel in the lowest bits (as nn::gfx names R10_G10_B10_A2). A missing blue reads as 0, a missing alpha
/// as 255.
/// </remarks>
public static class PixelFormats
{
    private readonly record struct Channel(int Shift, int Bits);

    private readonly record struct Packed(Channel R, Channel G, Channel B, Channel A);

    private static readonly Channel None = new(0, 0);

    private static readonly Dictionary<TextureFormat, Packed> PackedLayouts = new()
    {
        [TextureFormat.Rgb565] = new(new(11, 5), new(5, 6), new(0, 5), None),
        [TextureFormat.Rgba4] = new(new(8, 4), new(4, 4), new(0, 4), new(12, 4)),
        [TextureFormat.Rg4] = new(new(0, 4), new(4, 4), None, None),
        [TextureFormat.Bgr565] = new(new(0, 5), new(5, 6), new(11, 5), None),
        [TextureFormat.Abgr4] = new(new(12, 4), new(8, 4), new(4, 4), new(0, 4)),
        [TextureFormat.Rgb5A1] = new(new(0, 5), new(5, 5), new(10, 5), new(15, 1)),
        [TextureFormat.A1Bgr5] = new(new(11, 5), new(6, 5), new(1, 5), new(0, 1)),
        [TextureFormat.Bgr5A1] = new(new(10, 5), new(5, 5), new(0, 5), new(15, 1)),
    };

    // the formats ExpandToRgba8 and CollapseRgba8 handle
    public static bool CanRoundTripThroughRgba8(TextureFormat format)
        => format is TextureFormat.R8 or TextureFormat.RG8 || PackedLayouts.ContainsKey(format);

    public static byte[] ExpandToRgba8(TextureFormat format, ReadOnlySpan<byte> data, int pixelCount)
    {
        if (!CanRoundTripThroughRgba8(format)) throw new NotSupportedException($"{format} has no RGBA8 expansion.");
        byte[] output = new byte[checked(pixelCount * 4)];
        int bytes = format.BytesPerBlock();
        int available = Math.Min(pixelCount, data.Length / bytes);
        Span<byte> dst = output;

        for (int i = 0; i < available; i++)
        {
            Span<byte> px = dst.Slice(i * 4, 4);
            if (format == TextureFormat.R8)
            {
                px[0] = px[1] = px[2] = data[i];
                px[3] = 255;
            }
            else if (format == TextureFormat.RG8)
            {
                px[0] = data[i * 2];
                px[1] = data[i * 2 + 1];
                px[3] = 255;
            }
            else
            {
                var layout = PackedLayouts[format];
                int v = bytes == 1 ? data[i] : data[i * 2] | (data[i * 2 + 1] << 8);
                px[0] = Expand(v, layout.R, 0);
                px[1] = Expand(v, layout.G, 0);
                px[2] = Expand(v, layout.B, 0);
                px[3] = Expand(v, layout.A, 255);
            }
        }
        return output;
    }

    public static byte[] CollapseRgba8(TextureFormat format, ReadOnlySpan<byte> rgba, int pixelCount)
    {
        if (!CanRoundTripThroughRgba8(format)) throw new NotSupportedException($"{format} has no RGBA8 collapse.");
        int bytes = format.BytesPerBlock();
        byte[] output = new byte[checked(pixelCount * bytes)];
        int available = Math.Min(pixelCount, rgba.Length / 4);

        for (int i = 0; i < available; i++)
        {
            ReadOnlySpan<byte> px = rgba.Slice(i * 4, 4);
            if (format == TextureFormat.R8)
                output[i] = px[0];
            else if (format == TextureFormat.RG8)
            {
                output[i * 2] = px[0];
                output[i * 2 + 1] = px[1];
            }
            else
            {
                var layout = PackedLayouts[format];
                int v = Collapse(px[0], layout.R) | Collapse(px[1], layout.G) | Collapse(px[2], layout.B) | Collapse(px[3], layout.A);
                output[i * bytes] = (byte)v;
                if (bytes == 2) output[i * 2 + 1] = (byte)(v >> 8);
            }
        }
        return output;
    }

    // A channel widened to 8 bits by repeating its bits, so 0 stays 0 and all ones becomes 255.
    private static byte Expand(int value, Channel c, byte missing)
    {
        if (c.Bits == 0) return missing;
        int x = (value >> c.Shift) & ((1 << c.Bits) - 1);
        int r = 0, have = 0;
        while (have < 8) { r = (r << c.Bits) | x; have += c.Bits; }
        return (byte)(r >> (have - 8));
    }

    private static int Collapse(byte value, Channel c) => c.Bits == 0 ? 0 : (value >> (8 - c.Bits)) << c.Shift;
}
