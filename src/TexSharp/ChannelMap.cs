namespace TexSharp;

/// <summary>Where an output channel gets its value. The numbers are the ones BNTX stores.</summary>
public enum ChannelSource : byte
{
    Zero = 0,
    One = 1,
    Red = 2,
    Green = 3,
    Blue = 4,
    Alpha = 5,
}

/// <summary>
/// The channel swizzle a texture carries: for each of red, green, blue and alpha, which decoded channel (or a
/// constant) the shader reads. Decoding gives the texture's channels as stored; this is how they're meant to be seen.
/// </summary>
public readonly record struct ChannelMap(ChannelSource Red, ChannelSource Green, ChannelSource Blue, ChannelSource Alpha)
{
    public static ChannelMap Identity { get; } = new(ChannelSource.Red, ChannelSource.Green, ChannelSource.Blue, ChannelSource.Alpha);

    public bool IsIdentity => this == Identity;

    /// <summary>
    /// A tangent-space normal map as the game stores one in BC5: red and green carry X and Y, and blue and alpha are
    /// constants because the shader rebuilds Z.
    /// </summary>
    public bool IsTangentNormal
        => Red == ChannelSource.Red && Green == ChannelSource.Green
           && Blue is ChannelSource.Zero or ChannelSource.One && Alpha is ChannelSource.Zero or ChannelSource.One;

    /// <summary>From the four bytes BNTX stores. A value that isn't a channel reads red.</summary>
    public static ChannelMap FromBntx(byte red, byte green, byte blue, byte alpha)
        => new(Source(red), Source(green), Source(blue), Source(alpha));

    /// <summary>From the four selector bytes a TXTG stores, which number the sources differently: 0 to 3 are R, G, B, A, then 4 is zero and 5 is one.</summary>
    public static ChannelMap FromTxtg(byte red, byte green, byte blue, byte alpha)
        => new(FromTxtgSelector(red, ChannelSource.Red), FromTxtgSelector(green, ChannelSource.Green),
               FromTxtgSelector(blue, ChannelSource.Blue), FromTxtgSelector(alpha, ChannelSource.Alpha));

    /// <summary>Rearranges RGBA8 pixels in place. Nothing happens for the identity map.</summary>
    public void Apply(Span<byte> rgba)
    {
        if (IsIdentity) return;

        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2], a = rgba[i + 3];
            rgba[i] = Pick(Red, r, g, b, a);
            rgba[i + 1] = Pick(Green, r, g, b, a);
            rgba[i + 2] = Pick(Blue, r, g, b, a);
            rgba[i + 3] = Pick(Alpha, r, g, b, a);
        }
    }

    private static byte Pick(ChannelSource source, byte r, byte g, byte b, byte a) => source switch
    {
        ChannelSource.Zero => 0,
        ChannelSource.One => 255,
        ChannelSource.Green => g,
        ChannelSource.Blue => b,
        ChannelSource.Alpha => a,
        _ => r,
    };

    private static ChannelSource Source(byte value) => value <= (byte)ChannelSource.Alpha ? (ChannelSource)value : ChannelSource.Red;

    private static ChannelSource FromTxtgSelector(byte selector, ChannelSource fallback) => selector switch
    {
        0 => ChannelSource.Red,
        1 => ChannelSource.Green,
        2 => ChannelSource.Blue,
        3 => ChannelSource.Alpha,
        4 => ChannelSource.Zero,
        5 => ChannelSource.One,
        _ => fallback,
    };
}
