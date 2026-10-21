namespace TexSharp;

/// <summary>Maps BNTX surface format ids to and from <see cref="TextureFormat"/>.</summary>
/// <remarks>
/// An id is the u32 BntxSharp calls <c>SurfaceFormat</c>: the high bytes pick the layout, in nn::gfx's ChannelFormat
/// order, and the low byte the variant, nn::gfx's TypeFormat. Integer variants (UINT, SINT) read as the normalized
/// ones, and signed variants (SNORM, SINT and BC6's signed float) set snorm. ETC, EAC, PVRTC and layout 0x11 are not
/// read.
/// </remarks>
public static class BntxFormats
{
    private const uint Unorm = 0x01, Snorm = 0x02, Uint = 0x03, Sint = 0x04, Float = 0x05, Srgb = 0x06, Ufloat = 0x0A;

    // TotK's vertex animation textures (0x1505, four half floats a texel) confirm R16_G16_B16_A16 at 0x15.
    private static readonly Dictionary<uint, TextureFormat> FromBase = new()
    {
        [0x01] = TextureFormat.Rg4,
        [0x02] = TextureFormat.R8,
        [0x03] = TextureFormat.Rgba4,
        [0x04] = TextureFormat.Abgr4,
        [0x05] = TextureFormat.Rgb5A1,
        [0x06] = TextureFormat.A1Bgr5,
        [0x07] = TextureFormat.Rgb565,
        [0x08] = TextureFormat.Bgr565,
        [0x09] = TextureFormat.RG8,
        [0x0A] = TextureFormat.R16,
        [0x0B] = TextureFormat.Rgba8,
        [0x0C] = TextureFormat.Bgra8,
        [0x0D] = TextureFormat.Rgb9E5,
        [0x0E] = TextureFormat.Rgb10A2,
        [0x0F] = TextureFormat.R11G11B10Float,
        [0x10] = TextureFormat.B10G11R11Float,
        [0x12] = TextureFormat.RG16,
        [0x13] = TextureFormat.D24S8,
        [0x14] = TextureFormat.R32,
        [0x15] = TextureFormat.Rgba16,
        [0x16] = TextureFormat.D32FloatS8X24,
        [0x17] = TextureFormat.RG32,
        [0x18] = TextureFormat.Rgb32,
        [0x19] = TextureFormat.Rgba32,
        [0x1A] = TextureFormat.Bc1,
        [0x1B] = TextureFormat.Bc2,
        [0x1C] = TextureFormat.Bc3,
        [0x1D] = TextureFormat.Bc4,
        [0x1E] = TextureFormat.Bc5,
        [0x1F] = TextureFormat.Bc6,
        [0x20] = TextureFormat.Bc7,
        [0x3B] = TextureFormat.Bgr5A1,
    };

    // Layouts whose FLOAT variant is a different format. The packed floats and depth are only ever float.
    private static readonly Dictionary<uint, TextureFormat> FloatFromBase = new()
    {
        [0x0A] = TextureFormat.R16Float,
        [0x12] = TextureFormat.RG16Float,
        [0x14] = TextureFormat.R32Float,
        [0x15] = TextureFormat.Rgba16Float,
        [0x17] = TextureFormat.RG32Float,
        [0x18] = TextureFormat.Rgb32Float,
        [0x19] = TextureFormat.Rgba32Float,
    };

    private static readonly Dictionary<TextureFormat, uint> ToBase = BuildToBase();

    private static Dictionary<TextureFormat, uint> BuildToBase()
    {
        Dictionary<TextureFormat, uint> map = [];
        foreach (var (id, format) in FromBase.Concat(FloatFromBase))
            map[format] = id;
        for (uint i = 0; i < 14; i++)
            map[TextureFormat.Astc4x4 + (int)i] = 0x2D + i;
        return map;
    }

    public static bool TryFromBntx(uint formatId, out TextureFormat format, out bool srgb, out bool snorm)
    {
        uint layout = formatId >> 8;
        uint variant = formatId & 0xFF;
        srgb = variant == Srgb;
        snorm = variant is Snorm or Sint || (layout == 0x1F && variant == Float);

        if (layout is >= 0x2D and <= 0x3A)
        {
            format = TextureFormat.Astc4x4 + (int)(layout - 0x2D);
            return true;
        }
        if (variant == Float && FloatFromBase.TryGetValue(layout, out format))
            return true;
        return FromBase.TryGetValue(layout, out format);
    }

    public static uint ToBntx(TextureFormat format, bool srgb = false, bool snorm = false)
    {
        uint variant = format switch
        {
            TextureFormat.Bc6 => snorm ? Float : Ufloat,
            TextureFormat.D32FloatS8X24 => Float,
            _ when format.IsFloat() => Float,
            // 32-bit integer channels have no normalized variant.
            TextureFormat.R32 or TextureFormat.RG32 or TextureFormat.Rgb32 or TextureFormat.Rgba32 => snorm ? Sint : Uint,
            _ => srgb ? Srgb : snorm ? Snorm : Unorm,
        };
        return (ToBase[format] << 8) | variant;
    }
}
