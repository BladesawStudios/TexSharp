namespace TexSharp;

/// <summary>
/// The pixel layouts a BNTX texture can hold that DDS can carry. Colour space (sRGB, signed) is kept
/// separately, because it changes how the bytes are read but not how big they are.
/// </summary>
public enum TextureFormat
{
    R8,
    RG8,
    Rgb565,
    Rgba4,
    Rgb10A2,
    Rgba8,
    Bgra8,

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
        TextureFormat.R8 => 1,
        TextureFormat.RG8 or TextureFormat.Rgb565 or TextureFormat.Rgba4 => 2,
        TextureFormat.Rgb10A2 or TextureFormat.Rgba8 or TextureFormat.Bgra8 => 4,
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
