namespace TexSharp;

/// <summary>The pixel layouts a BNTX texture can hold.</summary>
/// <remarks>
/// All but <see cref="B10G11R11Float"/> have a DDS equivalent. Colour space (sRGB, signed) is kept separately, because
/// it changes how the bytes are read but not how big they are.
/// </remarks>
public enum TextureFormat
{
    R8,
    RG8,
    Rgb565,
    Rgba4,
    Rgb10A2,
    Rgba8,
    Bgra8,

    // The other small packed layouts. Each is named for its channels from the lowest bit up.
    Rg4,
    Bgr565,
    Abgr4,
    Rgb5A1,
    A1Bgr5,
    Bgr5A1,

    // More than 8 bits a channel. The integer ones are read as normalized; the float ones are separate formats,
    // because the same bytes mean something else as floats.
    R16,
    RG16,
    Rgba16,
    R32,
    RG32,
    Rgb32,
    Rgba32,
    R16Float,
    RG16Float,
    Rgba16Float,
    R32Float,
    RG32Float,
    Rgb32Float,
    Rgba32Float,
    R11G11B10Float,
    B10G11R11Float,
    Rgb9E5,

    // Depth in red, stencil in green.
    D24S8,
    D32FloatS8X24,

    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc5,
    Bc6,
    Bc7,

    // Declared in the order Switch Toolbox lays out its DXGI values, which DdsImage relies on.
    Astc4x4,
    Astc5x4,
    Astc5x5,
    Astc6x5,
    Astc6x6,
    Astc8x5,
    Astc8x6,
    Astc8x8,
    Astc10x5,
    Astc10x6,
    Astc10x8,
    Astc10x10,
    Astc12x10,
    Astc12x12,
}

public static class TextureFormats
{
    private const int FirstAstc = (int)TextureFormat.Astc4x4;

    private static readonly (int Width, int Height)[] AstcFootprints =
    [
        (4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6),
        (8, 8), (10, 5), (10, 6), (10, 8), (10, 10), (12, 10), (12, 12),
    ];

    public static bool IsAstc(this TextureFormat format) => format >= TextureFormat.Astc4x4;

    /// <summary>True for BCn and ASTC: formats stored as blocks rather than pixels.</summary>
    public static bool IsBlockCompressed(this TextureFormat format) => format >= TextureFormat.Bc1;

    // the formats whose channels are floats, which an 8-bit image can only show clamped to 0..1
    public static bool IsFloat(this TextureFormat format) => format is >= TextureFormat.R16Float and <= TextureFormat.Rgb9E5;

    public static int BlockWidth(this TextureFormat format) => format switch
    {
        >= TextureFormat.Astc4x4 => AstcFootprints[(int)format - FirstAstc].Width,
        >= TextureFormat.Bc1 => 4,
        _ => 1,
    };

    public static int BlockHeight(this TextureFormat format) => format switch
    {
        >= TextureFormat.Astc4x4 => AstcFootprints[(int)format - FirstAstc].Height,
        >= TextureFormat.Bc1 => 4,
        _ => 1,
    };

    /// <summary>Bytes per block, or per pixel for the uncompressed formats.</summary>
    public static int BytesPerBlock(this TextureFormat format) => format switch
    {
        TextureFormat.R8 or TextureFormat.Rg4 => 1,
        TextureFormat.RG8 or TextureFormat.Rgb565 or TextureFormat.Rgba4 or TextureFormat.Bgr565 or TextureFormat.Abgr4
            or TextureFormat.Rgb5A1 or TextureFormat.A1Bgr5 or TextureFormat.Bgr5A1 or TextureFormat.R16 or TextureFormat.R16Float => 2,
        TextureFormat.Rgb10A2 or TextureFormat.Rgba8 or TextureFormat.Bgra8 or TextureFormat.RG16 or TextureFormat.RG16Float
            or TextureFormat.R32 or TextureFormat.R32Float or TextureFormat.R11G11B10Float or TextureFormat.B10G11R11Float
            or TextureFormat.Rgb9E5 or TextureFormat.D24S8 => 4,
        TextureFormat.Rgba16 or TextureFormat.Rgba16Float or TextureFormat.RG32 or TextureFormat.RG32Float or TextureFormat.D32FloatS8X24 => 8,
        TextureFormat.Rgb32 or TextureFormat.Rgb32Float => 12,
        TextureFormat.Bc1 or TextureFormat.Bc4 => 8,
        _ => 16,
    };

    /// <summary>The size in bytes of one mip level of the given dimensions.</summary>
    public static int LevelSize(this TextureFormat format, int width, int height)
    {
        long blocks = (long)DivRoundUp(width, format.BlockWidth()) * DivRoundUp(height, format.BlockHeight());
        return checked((int)(blocks * format.BytesPerBlock()));
    }

    /// <summary>The ASTC format with the given footprint, or null if there isn't one.</summary>
    public static TextureFormat? AstcFromFootprint(int blockWidth, int blockHeight)
    {
        int index = Array.IndexOf(AstcFootprints, (blockWidth, blockHeight));
        return index < 0 ? null : (TextureFormat)(FirstAstc + index);
    }

    internal static int DivRoundUp(int n, int d) => (n + d - 1) / d;
}
