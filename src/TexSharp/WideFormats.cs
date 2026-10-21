using System.Buffers.Binary;

namespace TexSharp;

/// <summary>The formats with more than 8 bits a channel, to and from RGBA8.</summary>
/// <remarks>
/// Covers 16 and 32-bit integers, half and single floats, the packed floats and depth. Integer channels are read as
/// normalized, so an R16 heightmap (UNORM or UINT) goes from black at 0 to white at 65535; signed ones go through
/// <see cref="SignedValues"/>. Floats are clamped to 0..1, depth comes out in red with stencil in green, and one-channel
/// formats come out as grey, as R8 does. RGB9E5 and depth can't be encoded.
/// </remarks>
internal static class WideFormats
{
    public static bool Handles(TextureFormat format) => format is >= TextureFormat.R16 and <= TextureFormat.D32FloatS8X24;

    public static void Decode(TextureFormat format, ReadOnlySpan<byte> data, int pixelCount, bool snorm, Span<byte> rgba)
    {
        int stride = format.BytesPerBlock();
        Span<float> c = stackalloc float[4];
        for (int i = 0; i < pixelCount; i++)
        {
            c[0] = c[1] = c[2] = 0;
            c[3] = 1;
            if (Read(format, data.Slice(i * stride, stride), snorm, c) == 1)
                c[1] = c[2] = c[0];
            for (int ch = 0; ch < 4; ch++)
                rgba[i * 4 + ch] = ToByte(c[ch]);
        }
    }

    // why a format can't be made from an 8-bit image, or null if it can
    public static string? WhyNotEncodable(TextureFormat format) => format switch
    {
        TextureFormat.Rgb9E5 => "the shared-exponent format isn't encodable.",
        TextureFormat.D24S8 or TextureFormat.D32FloatS8X24 => "depth and stencil can't be made from an image.",
        _ => null,
    };

    public static byte[] Encode(TextureFormat format, ReadOnlySpan<byte> rgba, int pixelCount)
    {
        int stride = format.BytesPerBlock();
        int channels = Channels(format);
        byte[] output = new byte[checked(pixelCount * stride)];
        for (int i = 0; i < pixelCount; i++)
        {
            ReadOnlySpan<byte> px = rgba.Slice(i * 4, 4);
            Span<byte> dst = output.AsSpan(i * stride, stride);
            switch (format)
            {
                case TextureFormat.R11G11B10Float:
                    BinaryPrimitives.WriteUInt32LittleEndian(dst, SmallFloat(px[0], 4) | (SmallFloat(px[1], 4) << 11) | (SmallFloat(px[2], 5) << 22));
                    break;
                case TextureFormat.B10G11R11Float:
                    BinaryPrimitives.WriteUInt32LittleEndian(dst, SmallFloat(px[2], 5) | (SmallFloat(px[1], 4) << 10) | (SmallFloat(px[0], 4) << 21));
                    break;
                default:
                    int size = stride / channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        Span<byte> at = dst.Slice(ch * size, size);
                        byte v = px[ch];
                        if (format.IsFloat() && size == 2) BinaryPrimitives.WriteHalfLittleEndian(at, (Half)(v / 255f));
                        else if (format.IsFloat()) BinaryPrimitives.WriteSingleLittleEndian(at, v / 255f);
                        else if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)(v * 257));
                        else BinaryPrimitives.WriteUInt32LittleEndian(at, v * 0x01010101u);
                    }
                    break;
            }
        }
        return output;
    }

    private static int Channels(TextureFormat format) => format switch
    {
        TextureFormat.R16 or TextureFormat.R32 or TextureFormat.R16Float or TextureFormat.R32Float => 1,
        TextureFormat.RG16 or TextureFormat.RG32 or TextureFormat.RG16Float or TextureFormat.RG32Float => 2,
        TextureFormat.Rgb32 or TextureFormat.Rgb32Float => 3,
        _ => 4,
    };

    // Fills c with the channels the pixel holds, each 0..1 (or outside it for floats), and returns how many.
    private static int Read(TextureFormat format, ReadOnlySpan<byte> px, bool snorm, Span<float> c)
    {
        switch (format)
        {
            case TextureFormat.R11G11B10Float:
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(px);
                c[0] = SmallFloat(v & 0x7FF, 6);
                c[1] = SmallFloat((v >> 11) & 0x7FF, 6);
                c[2] = SmallFloat(v >> 22, 5);
                return 3;
            }
            case TextureFormat.B10G11R11Float:
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(px);
                c[2] = SmallFloat(v & 0x3FF, 5);
                c[1] = SmallFloat((v >> 10) & 0x7FF, 6);
                c[0] = SmallFloat(v >> 21, 6);
                return 3;
            }
            case TextureFormat.Rgb9E5:
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(px);
                float scale = MathF.Pow(2, (int)(v >> 27) - 15 - 9);
                c[0] = (v & 0x1FF) * scale;
                c[1] = ((v >> 9) & 0x1FF) * scale;
                c[2] = ((v >> 18) & 0x1FF) * scale;
                return 3;
            }
            case TextureFormat.D24S8:
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(px);
                c[0] = (v & 0xFFFFFF) / 16777215f;
                c[1] = (v >> 24) / 255f;
                return 2;
            }
            case TextureFormat.D32FloatS8X24:
                c[0] = BinaryPrimitives.ReadSingleLittleEndian(px);
                c[1] = px[4] / 255f;
                return 2;
        }

        int channels = Channels(format);
        int size = px.Length / channels;
        for (int ch = 0; ch < channels; ch++)
        {
            ReadOnlySpan<byte> at = px.Slice(ch * size, size);
            c[ch] = (format.IsFloat(), size, snorm) switch
            {
                (true, 2, _) => (float)BinaryPrimitives.ReadHalfLittleEndian(at),
                (true, _, _) => BinaryPrimitives.ReadSingleLittleEndian(at),
                (false, 2, false) => BinaryPrimitives.ReadUInt16LittleEndian(at) / 65535f,
                (false, 2, true) => SignedValues.ToUnit(BinaryPrimitives.ReadInt16LittleEndian(at) / 32767.0),
                (false, _, false) => (float)(BinaryPrimitives.ReadUInt32LittleEndian(at) / 4294967295.0),
                (false, _, true) => SignedValues.ToUnit(BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483647.0),
            };
        }
        return channels;
    }

    // An unsigned float of 5 exponent bits and the given mantissa bits (R11G11B10's 6 and 5).
    private static float SmallFloat(uint bits, int mantissaBits)
    {
        uint exponent = bits >> mantissaBits, mantissa = bits & ((1u << mantissaBits) - 1);
        float fraction = mantissa / (float)(1 << mantissaBits);
        if (exponent == 0) return fraction * MathF.Pow(2, -14);
        if (exponent == 31) return mantissa == 0 ? float.PositiveInfinity : float.NaN;
        return (1 + fraction) * MathF.Pow(2, (int)exponent - 15);
    }

    // The other way: a half float has the same exponent, so the small float is its top bits. Truncates.
    private static uint SmallFloat(byte value, int dropBits)
        => (uint)BitConverter.HalfToUInt16Bits((Half)(value / 255f)) >> dropBits;

    private static byte ToByte(float f)
    {
        if (!(f > 0)) return 0;
        if (f >= 1) return 255;
        return (byte)(f * 255 + 0.5f);
    }
}
