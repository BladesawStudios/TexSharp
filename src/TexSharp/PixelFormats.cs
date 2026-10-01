namespace TexSharp;

/// <summary>
/// Conversions between the small uncompressed layouts and RGBA8. Most image editors won't open a one or
/// two channel DDS or a packed 16-bit one, so these are exported as RGBA8 and collapsed back on import.
/// Every channel is 8 bits or fewer, so for an unedited image expanding then collapsing gives the
/// original bytes. Rgb10A2 doesn't fit in 8 bits per channel and is left out of the pair.
/// </summary>
public static class PixelFormats
{
    /// <summary>True for the formats <see cref="ExpandToRgba8"/> and <see cref="CollapseRgba8"/> handle.</summary>
    public static bool CanRoundTripThroughRgba8(TextureFormat format)
        => format is TextureFormat.R8 or TextureFormat.RG8 or TextureFormat.Rgb565 or TextureFormat.Rgba4;

    public static byte[] ExpandToRgba8(TextureFormat format, ReadOnlySpan<byte> data, int pixelCount)
    {
        byte[] output = new byte[checked(pixelCount * 4)];
        int available = Math.Min(pixelCount, data.Length / format.BytesPerBlock());
        Span<byte> dst = output;

        for (int i = 0; i < available; i++)
        {
            Span<byte> px = dst.Slice(i * 4, 4);
            switch (format)
            {
                case TextureFormat.R8:
                    px[0] = px[1] = px[2] = data[i];
                    px[3] = 255;
                    break;
                case TextureFormat.RG8:
                    px[0] = data[i * 2];
                    px[1] = data[i * 2 + 1];
                    px[3] = 255;
                    break;
                case TextureFormat.Rgb565:
                {
                    int v = data[i * 2] | (data[i * 2 + 1] << 8);
                    int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
                    px[0] = (byte)((r << 3) | (r >> 2));
                    px[1] = (byte)((g << 2) | (g >> 4));
                    px[2] = (byte)((b << 3) | (b >> 2));
                    px[3] = 255;
                    break;
                }
                case TextureFormat.Rgba4:
                {
                    int v = data[i * 2] | (data[i * 2 + 1] << 8);
                    px[0] = (byte)(((v >> 8) & 0xF) * 17);
                    px[1] = (byte)(((v >> 4) & 0xF) * 17);
                    px[2] = (byte)((v & 0xF) * 17);
                    px[3] = (byte)(((v >> 12) & 0xF) * 17);
                    break;
                }
                default:
                    throw new NotSupportedException($"{format} has no RGBA8 expansion.");
            }
        }
        return output;
    }

    public static byte[] CollapseRgba8(TextureFormat format, ReadOnlySpan<byte> rgba, int pixelCount)
    {
        if (!CanRoundTripThroughRgba8(format)) throw new NotSupportedException($"{format} has no RGBA8 collapse.");
        byte[] output = new byte[checked(pixelCount * format.BytesPerBlock())];
        int available = Math.Min(pixelCount, rgba.Length / 4);

        for (int i = 0; i < available; i++)
        {
            ReadOnlySpan<byte> px = rgba.Slice(i * 4, 4);
            switch (format)
            {
                case TextureFormat.R8:
                    output[i] = px[0];
                    break;
                case TextureFormat.RG8:
                    output[i * 2] = px[0];
                    output[i * 2 + 1] = px[1];
                    break;
                case TextureFormat.Rgb565:
                {
                    int v = ((px[0] >> 3) << 11) | ((px[1] >> 2) << 5) | (px[2] >> 3);
                    output[i * 2] = (byte)v;
                    output[i * 2 + 1] = (byte)(v >> 8);
                    break;
                }
                case TextureFormat.Rgba4:
                {
                    int v = ((px[3] >> 4) << 12) | ((px[0] >> 4) << 8) | ((px[1] >> 4) << 4) | (px[2] >> 4);
                    output[i * 2] = (byte)v;
                    output[i * 2 + 1] = (byte)(v >> 8);
                    break;
                }
            }
        }
        return output;
    }
}
