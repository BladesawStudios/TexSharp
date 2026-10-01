namespace TexSharp;

/// <summary>
/// Maps BNTX surface format ids (the u32 BntxSharp calls <c>SurfaceFormat</c>: the high bytes pick the
/// layout, the low byte the variant) to and from <see cref="TextureFormat"/>.
/// </summary>
public static class BntxFormats
{
    // Layouts DDS has no separate type for are carried as the same-sized one: the bytes round-trip
    // untouched, but their channels are not read as the format the BNTX declares.
    private static readonly Dictionary<uint, TextureFormat> FromBase = new()
    {
        [0x02] = TextureFormat.R8,
        [0x09] = TextureFormat.RG8,
        [0x07] = TextureFormat.Rgb565,
        [0x08] = TextureFormat.Rgb565,
        [0x05] = TextureFormat.Rgb565,
        [0x06] = TextureFormat.Rgb565,
        [0x3B] = TextureFormat.Rgb565,
        [0x03] = TextureFormat.Rgba4,
        [0x04] = TextureFormat.Rgba4,
        [0x0E] = TextureFormat.Rgb10A2,
        [0x0B] = TextureFormat.Rgba8,
        [0x0C] = TextureFormat.Bgra8,
        [0x1A] = TextureFormat.Bc1,
        [0x1B] = TextureFormat.Bc2,
        [0x1C] = TextureFormat.Bc3,
        [0x1D] = TextureFormat.Bc4,
        [0x1E] = TextureFormat.Bc5,
        [0x1F] = TextureFormat.Bc6,
        [0x20] = TextureFormat.Bc7,
    };

    private static readonly Dictionary<TextureFormat, uint> ToBase = BuildToBase();

    private static Dictionary<TextureFormat, uint> BuildToBase()
    {
        // The first id listed for a format wins, which keeps Rgb565 as the real R5G6B5 (0x07).
        Dictionary<TextureFormat, uint> map = [];
        foreach (var (id, format) in FromBase.OrderBy(pair => pair.Key))
            map.TryAdd(format, id);
        map[TextureFormat.Rgb565] = 0x07;
        for (uint i = 0; i < 14; i++)
            map[TextureFormat.Astc4x4 + (int)i] = 0x2D + i;
        return map;
    }

    /// <summary>The DDS-side format for a BNTX surface format id, or false if DDS can't carry it.</summary>
    public static bool TryFromBntx(uint formatId, out TextureFormat format, out bool srgb, out bool snorm)
    {
        uint layout = formatId >> 8;
        uint variant = formatId & 0xFF;
        srgb = variant == 0x06;
        snorm = variant == 0x02;

        if (layout is >= 0x2D and <= 0x3A)
        {
            format = TextureFormat.Astc4x4 + (int)(layout - 0x2D);
            return true;
        }
        return FromBase.TryGetValue(layout, out format);
    }

    /// <summary>The BNTX surface format id to write for a format and colour space.</summary>
    public static uint ToBntx(TextureFormat format, bool srgb = false, bool snorm = false)
    {
        uint variant = srgb ? 0x06u : snorm ? 0x02u : 0x01u;
        return (ToBase[format] << 8) | variant;
    }
}
