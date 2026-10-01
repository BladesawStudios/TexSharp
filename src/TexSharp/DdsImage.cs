using System.Buffers.Binary;

namespace TexSharp;

/// <summary>
/// A 2D DDS with raw payload: block or pixel data, linear, split per mip. Nothing is decoded or
/// re-encoded, so a BNTX level goes to DDS and back unchanged. Files are laid out the way Switch
/// Toolbox writes them, so both tools read each other's output:
/// BC1-BC5 use legacy FourCCs, RGBA8, BGRA8, BC6, BC7 and ASTC use the DX10 header with the unofficial ASTC DXGI
/// values (<c>ASTC_4x4_UNORM = 134</c> ... <c>ASTC_12x12_UNORM_SRGB = 187</c>), and plain colour
/// formats use pixel masks.
/// </summary>
public sealed class DdsImage
{
    private const int HeaderSize = 128;
    private const int Dx10HeaderSize = 20;
    private const int FirstAstcDxgi = 134;

    private const uint FlagsCaps = 0x1, FlagsHeight = 0x2, FlagsWidth = 0x4, FlagsPitch = 0x8, FlagsPixelFormat = 0x1000;
    private const uint FlagsMipCount = 0x20000, FlagsLinearSize = 0x80000;
    private const uint CapsComplex = 0x8, CapsTexture = 0x1000, CapsMipMap = 0x400000;
    private const uint PixelAlpha = 0x1, PixelFourCc = 0x4, PixelRgb = 0x40;

    private static readonly uint FourCcDx10 = FourCc("DX10");

    private static readonly Dictionary<uint, (TextureFormat Format, bool Srgb, bool Snorm)> FromFourCc = new()
    {
        [FourCc("DXT1")] = (TextureFormat.Bc1, false, false),
        [FourCc("DXT3")] = (TextureFormat.Bc2, false, false),
        [FourCc("DXT5")] = (TextureFormat.Bc3, false, false),
        [FourCc("BC4U")] = (TextureFormat.Bc4, false, false),
        [FourCc("BC4S")] = (TextureFormat.Bc4, false, true),
        [FourCc("ATI1")] = (TextureFormat.Bc4, false, false),
        [FourCc("BC5U")] = (TextureFormat.Bc5, false, false),
        [FourCc("BC5S")] = (TextureFormat.Bc5, false, true),
        [FourCc("ATI2")] = (TextureFormat.Bc5, false, false),
    };

    // DXGI value -> format, for the non-ASTC DX10 formats. ASTC is computed from FirstAstcDxgi.
    private static readonly Dictionary<uint, (TextureFormat Format, bool Srgb, bool Snorm)> FromDxgi = new()
    {
        [28] = (TextureFormat.Rgba8, false, false),
        [29] = (TextureFormat.Rgba8, true, false),
        [87] = (TextureFormat.Bgra8, false, false),
        [91] = (TextureFormat.Bgra8, true, false),
        [71] = (TextureFormat.Bc1, false, false),
        [72] = (TextureFormat.Bc1, true, false),
        [74] = (TextureFormat.Bc2, false, false),
        [75] = (TextureFormat.Bc2, true, false),
        [77] = (TextureFormat.Bc3, false, false),
        [78] = (TextureFormat.Bc3, true, false),
        [80] = (TextureFormat.Bc4, false, false),
        [81] = (TextureFormat.Bc4, false, true),
        [83] = (TextureFormat.Bc5, false, false),
        [84] = (TextureFormat.Bc5, false, true),
        [95] = (TextureFormat.Bc6, false, false),
        [96] = (TextureFormat.Bc6, false, true),
        [98] = (TextureFormat.Bc7, false, false),
        [99] = (TextureFormat.Bc7, true, false),
    };

    // Uncompressed layouts: bits per pixel and the R, G, B, A masks. A zero alpha mask means no alpha.
    private static readonly Dictionary<TextureFormat, (uint Bits, uint R, uint G, uint B, uint A)> Masks = new()
    {
        [TextureFormat.Rgba8] = (32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000),
        [TextureFormat.Bgra8] = (32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
        [TextureFormat.R8] = (8, 0x000000FF, 0, 0, 0),
        [TextureFormat.RG8] = (16, 0x000000FF, 0x0000FF00, 0, 0),
        [TextureFormat.Rgb565] = (16, 0xF800, 0x07E0, 0x001F, 0),
        [TextureFormat.Rgba4] = (16, 0x0F00, 0x00F0, 0x000F, 0xF000),
        [TextureFormat.Rgb10A2] = (32, 0x000003FF, 0x000FFC00, 0x3FF00000, 0xC0000000),
    };

    public DdsImage(int width, int height, TextureFormat format, IReadOnlyList<byte[]> mips, bool srgb = false, bool snorm = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(mips);
        Width = width;
        Height = height;
        Format = format;
        Mips = mips;
        IsSrgb = srgb;
        IsSnorm = snorm;
        ColorSpaceKnown = srgb || snorm || format is TextureFormat.Rgba8 or TextureFormat.Bgra8 or TextureFormat.Bc6 or TextureFormat.Bc7 || format.IsAstc();
    }

    public int Width { get; }
    public int Height { get; }
    public TextureFormat Format { get; }
    public bool IsSrgb { get; }
    public bool IsSnorm { get; }

    /// <summary>
    /// Whether the image says anything about colour space. A file with a legacy FourCC (BC1 to BC5) or pixel
    /// masks can't, so <see cref="IsSrgb"/> being false there means "not stated", not "linear"; import code should
    /// leave the target's colour space alone. Files with a DX10 header can.
    /// </summary>
    public bool ColorSpaceKnown { get; private init; }

    /// <summary>Raw level data, largest first.</summary>
    public IReadOnlyList<byte[]> Mips { get; }

    public static bool IsDds(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("DDS "u8);

    public static DdsImage Parse(ReadOnlySpan<byte> data)
    {
        if (!IsDds(data)) throw new InvalidDataException("Not a DDS file.");
        if (data.Length < HeaderSize) throw new InvalidDataException("DDS header truncated.");

        int height = ReadInt(data, 12);
        int width = ReadInt(data, 16);
        int mipCount = Math.Max(1, ReadInt(data, 28));
        if (width <= 0 || height <= 0) throw new InvalidDataException("DDS has no size.");

        uint pixelFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
        uint rgbBits = BinaryPrimitives.ReadUInt32LittleEndian(data[88..]);

        int payload = HeaderSize;
        bool dx10Header = false;
        TextureFormat format;
        bool srgb = false, snorm = false;

        if ((pixelFlags & PixelFourCc) != 0 && fourCc == FourCcDx10)
        {
            if (data.Length < HeaderSize + Dx10HeaderSize) throw new InvalidDataException("DDS DX10 header truncated.");
            uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(data[HeaderSize..]);
            payload = HeaderSize + Dx10HeaderSize;
            dx10Header = true;
            if (!TryFormatFromDxgi(dxgi, out format, out srgb, out snorm))
                throw new NotSupportedException($"Unsupported DXGI format {dxgi} in DDS.");
        }
        else if ((pixelFlags & PixelFourCc) != 0 && FromFourCc.TryGetValue(fourCc, out var legacy))
        {
            (format, srgb, snorm) = legacy;
        }
        else if ((pixelFlags & (PixelRgb | PixelAlpha)) != 0)
        {
            uint r = BinaryPrimitives.ReadUInt32LittleEndian(data[92..]);
            uint g = BinaryPrimitives.ReadUInt32LittleEndian(data[96..]);
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(data[100..]);
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(data[104..]);
            format = MatchMasks(rgbBits, r, g, b, a)
                ?? throw new NotSupportedException($"Unsupported uncompressed DDS ({rgbBits}bpp, R=0x{r:X} A=0x{a:X}).");
        }
        else
        {
            throw new NotSupportedException($"Unrecognised DDS pixel format (fourcc=0x{fourCc:X8}).");
        }

        List<byte[]> mips = [];
        int cursor = payload;
        for (int mip = 0; mip < mipCount; mip++)
        {
            int size = format.LevelSize(Math.Max(1, width >> mip), Math.Max(1, height >> mip));
            if (data.Length - cursor < size)
            {
                if (mip == 0) throw new InvalidDataException("DDS payload too small for its declared dimensions.");
                break;
            }
            mips.Add(data.Slice(cursor, size).ToArray());
            cursor += size;
        }

        return new DdsImage(width, height, format, mips, srgb, snorm) { ColorSpaceKnown = dx10Header };
    }

    public byte[] ToBytes()
    {
        int mipCount = Math.Max(1, Mips.Count);
        bool blockBased = Format.IsBlockCompressed();
        int bytesPerBlock = Format.BytesPerBlock();

        uint flags = FlagsCaps | FlagsHeight | FlagsWidth | FlagsPixelFormat;
        uint caps = CapsTexture;
        if (mipCount > 1)
        {
            flags |= FlagsMipCount;
            caps |= CapsComplex | CapsMipMap;
        }
        flags |= blockBased ? FlagsLinearSize : FlagsPitch;

        uint? legacyFourCc = LegacyFourCc(Format, IsSnorm);
        // Rgba8 and Bgra8 could be written with masks, but Switch Toolbox gives them a DX10 header too.
        bool dx10 = Format is TextureFormat.Rgba8 or TextureFormat.Bgra8 or TextureFormat.Bc6 or TextureFormat.Bc7 || Format.IsAstc();

        int payloadSize = Mips.Sum(mip => mip.Length);
        byte[] output = new byte[HeaderSize + (dx10 ? Dx10HeaderSize : 0) + payloadSize];
        Span<byte> header = output;

        "DDS "u8.CopyTo(header);
        WriteUInt(header, 4, 124);
        WriteUInt(header, 8, flags);
        WriteUInt(header, 12, (uint)Height);
        WriteUInt(header, 16, (uint)Width);
        WriteUInt(header, 20, blockBased ? (uint)Format.LevelSize(Width, Height) : (uint)(Width * bytesPerBlock));
        WriteUInt(header, 28, (uint)mipCount);
        WriteUInt(header, 76, 32);

        if (legacyFourCc is not null || dx10)
        {
            WriteUInt(header, 80, PixelFourCc);
            WriteUInt(header, 84, dx10 ? FourCcDx10 : legacyFourCc!.Value);
        }
        else
        {
            var (bits, r, g, b, a) = Masks[Format];
            WriteUInt(header, 80, PixelRgb | (a != 0 ? PixelAlpha : 0));
            WriteUInt(header, 88, bits);
            WriteUInt(header, 92, r);
            WriteUInt(header, 96, g);
            WriteUInt(header, 100, b);
            WriteUInt(header, 104, a);
        }
        WriteUInt(header, 108, caps);

        int cursor = HeaderSize;
        if (dx10)
        {
            WriteUInt(header, HeaderSize, DxgiFor(Format, IsSrgb, IsSnorm));
            WriteUInt(header, HeaderSize + 4, 3); // 2D
            WriteUInt(header, HeaderSize + 12, 1); // array size
            cursor += Dx10HeaderSize;
        }

        foreach (byte[] mip in Mips)
        {
            mip.CopyTo(output, cursor);
            cursor += mip.Length;
        }
        return output;
    }

    private static bool TryFormatFromDxgi(uint dxgi, out TextureFormat format, out bool srgb, out bool snorm)
    {
        if (FromDxgi.TryGetValue(dxgi, out var entry))
        {
            (format, srgb, snorm) = entry;
            return true;
        }

        uint offset = dxgi - FirstAstcDxgi;
        if (dxgi >= FirstAstcDxgi && offset < 14 * 4 && (offset & 3) < 2)
        {
            format = TextureFormat.Astc4x4 + (int)(offset >> 2);
            srgb = (offset & 3) == 1;
            snorm = false;
            return true;
        }

        format = default;
        srgb = snorm = false;
        return false;
    }

    private static uint DxgiFor(TextureFormat format, bool srgb, bool snorm)
    {
        if (format.IsAstc())
            return (uint)(FirstAstcDxgi + 4 * (format - TextureFormat.Astc4x4) + (srgb ? 1 : 0));

        return format switch
        {
            TextureFormat.Rgba8 => srgb ? 29u : 28u,
            TextureFormat.Bgra8 => srgb ? 91u : 87u,
            TextureFormat.Bc6 => snorm ? 96u : 95u,
            TextureFormat.Bc7 => srgb ? 99u : 98u,
            _ => throw new NotSupportedException($"{format} has no DXGI value."),
        };
    }

    private static uint? LegacyFourCc(TextureFormat format, bool snorm) => format switch
    {
        TextureFormat.Bc1 => FourCc("DXT1"),
        TextureFormat.Bc2 => FourCc("DXT3"),
        TextureFormat.Bc3 => FourCc("DXT5"),
        TextureFormat.Bc4 => snorm ? FourCc("BC4S") : FourCc("BC4U"),
        TextureFormat.Bc5 => snorm ? FourCc("BC5S") : FourCc("BC5U"),
        _ => null,
    };

    // A zero stored alpha mask means "don't care", so files that set it for an opaque layout still match.
    private static TextureFormat? MatchMasks(uint bits, uint r, uint g, uint b, uint a)
    {
        foreach (var (format, mask) in Masks)
            if (mask.Bits == bits && mask.R == r && mask.G == g && mask.B == b && (mask.A == a || mask.A == 0))
                return format;
        return null;
    }

    private static uint FourCc(string code) => BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(code));

    private static int ReadInt(ReadOnlySpan<byte> data, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        return value > int.MaxValue ? 0 : (int)value;
    }

    private static void WriteUInt(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
}
