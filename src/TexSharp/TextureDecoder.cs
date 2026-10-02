namespace TexSharp;

/// <summary>Decodes one linear mip level (as BntxSharp's deswizzle or a DDS gives it) to RGBA8.</summary>
public static class TextureDecoder
{
    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> pixels);

    /// <summary>
    /// Returns width * height * 4 bytes, row-major, in R, G, B, A order. BC4 comes out as grey and BC5 as
    /// red and green with blue zero; undoing a normal map's packing is left to the caller. HDR BC6 is
    /// clamped to 0..1.
    /// </summary>
    public static byte[] ToRgba8(TextureFormat format, ReadOnlySpan<byte> data, int width, int height, bool snorm = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int needed = format.LevelSize(width, height);
        if (data.Length < needed)
            throw new ArgumentException($"{format} {width}x{height} needs {needed} bytes, got {data.Length}.", nameof(data));

        int pixelCount = checked(width * height);
        byte[] rgba = new byte[checked(pixelCount * 4)];

        switch (format)
        {
            case TextureFormat.Rgba8:
                data[..rgba.Length].CopyTo(rgba);
                break;
            case TextureFormat.Bgra8:
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = data[i + 2];
                    rgba[i + 1] = data[i + 1];
                    rgba[i + 2] = data[i];
                    rgba[i + 3] = data[i + 3];
                }
                break;
            case TextureFormat.Rgb10A2:
                for (int i = 0; i < pixelCount; i++)
                {
                    uint v = BitConverter.ToUInt32(data[(i * 4)..]);
                    rgba[i * 4] = (byte)((v & 0x3FF) >> 2);
                    rgba[i * 4 + 1] = (byte)(((v >> 10) & 0x3FF) >> 2);
                    rgba[i * 4 + 2] = (byte)(((v >> 20) & 0x3FF) >> 2);
                    rgba[i * 4 + 3] = (byte)((v >> 30) * 85);
                }
                break;
            case TextureFormat.R8 or TextureFormat.RG8 or TextureFormat.Rgb565 or TextureFormat.Rgba4:
                return PixelFormats.ExpandToRgba8(format, data, pixelCount);

            case TextureFormat.Bc1:
                DecodeBlocks(data, width, height, 8, rgba, (b, p) => BcDecoder.DecodeBc1(b, p));
                break;
            case TextureFormat.Bc2:
                DecodeBlocks(data, width, height, 16, rgba, BcDecoder.DecodeBc2);
                break;
            case TextureFormat.Bc3:
                DecodeBlocks(data, width, height, 16, rgba, BcDecoder.DecodeBc3);
                break;
            case TextureFormat.Bc4:
                DecodeBlocks(data, width, height, 8, rgba, (b, p) => BcDecoder.DecodeBc4(b, p, snorm));
                break;
            case TextureFormat.Bc5:
                DecodeBlocks(data, width, height, 16, rgba, (b, p) => BcDecoder.DecodeBc5(b, p, snorm));
                break;
            case TextureFormat.Bc6:
                DecodeBlocks(data, width, height, 16, rgba, (b, p) => Bc6hDecoder.DecodeBlock(b, p, snorm));
                break;
            case TextureFormat.Bc7:
                DecodeBlocks(data, width, height, 16, rgba, Bc7Decoder.DecodeBlock);
                break;

            default:
                AstcDecoder.Decode(data, width, height, format.BlockWidth(), format.BlockHeight(), rgba);
                break;
        }
        return rgba;
    }

    /// <summary>
    /// <see cref="ToRgba8"/> as the texture is meant to be seen: the channel swizzle applied, and for a BC5 tangent-space
    /// normal map (see <see cref="ChannelMap.IsTangentNormal"/>) the blue channel rebuilt.
    /// </summary>
    public static byte[] Render(TextureFormat format, ReadOnlySpan<byte> data, int width, int height, ChannelMap channels, bool snorm = false)
    {
        byte[] rgba = ToRgba8(format, data, width, height, snorm);
        if (format == TextureFormat.Bc5 && channels.IsTangentNormal)
            ReconstructNormalZ(rgba);
        else
            channels.Apply(rgba);
        return rgba;
    }

    /// <summary>Rebuilds the blue channel of a tangent-space normal from the red and green BC5 gives, in place.</summary>
    public static void ReconstructNormalZ(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            float x = rgba[i] / 127.5f - 1f, y = rgba[i + 1] / 127.5f - 1f;
            float z = MathF.Sqrt(Math.Clamp(1f - x * x - y * y, 0f, 1f));
            rgba[i + 2] = (byte)Math.Clamp((z + 1f) * 127.5f, 0f, 255f);
        }
    }

    private static void DecodeBlocks(ReadOnlySpan<byte> data, int width, int height, int blockBytes, Span<byte> rgba, BlockDecoder decode)
    {
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        Span<byte> block = stackalloc byte[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                decode(data.Slice((by * blocksX + bx) * blockBytes, blockBytes), block);

                int copyWidth = Math.Min(4, width - bx * 4), copyHeight = Math.Min(4, height - by * 4);
                for (int y = 0; y < copyHeight; y++)
                    block.Slice(y * 16, copyWidth * 4).CopyTo(rgba[(((by * 4 + y) * width + bx * 4) * 4)..]);
            }
        }
    }
}
