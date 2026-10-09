namespace TexSharp;

/// <summary>Turns a DDS image into the mip data of a texture of another size, format or mip count.</summary>
public static class TextureConverter
{
    /// <summary>What <see cref="Convert"/> would have to change for the image to fit, as words ("resized from 64x64 to 128x128"); empty if nothing.</summary>
    public static List<string> Differences(DdsImage source, TextureFormat format, int width, int height, int mipCount)
    {
        List<string> changes = [];
        if (source.Width != width || source.Height != height) changes.Add($"resized from {source.Width}x{source.Height} to {width}x{height}");
        if (source.Format != format) changes.Add($"re-encoded from {source.Format} to {format}");
        if (source.Mips.Count != mipCount) changes.Add($"{source.Mips.Count} mip{(source.Mips.Count == 1 ? "" : "s")} became {mipCount}");
        return changes;
    }

    /// <summary>
    /// The mips (<paramref name="mipCount"/> of them, each in <paramref name="format"/>) for a texture of the given size.
    /// The picture is resized if the size differs; if it is the right size and has enough mips, those are kept (re-encoded),
    /// otherwise the chain is made again from the top level. <paramref name="srgb"/> makes the resizing work in linear light.
    /// </summary>
    public static List<byte[]> Convert(DdsImage source, TextureFormat format, int width, int height, int mipCount, bool srgb = false, bool snorm = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mipCount);
        string? why = TextureEncoder.WhyNotEncodable(format, snorm);
        if (why is not null) throw new NotSupportedException($"Can't convert to {format}: {why}");
        if (source.Mips.Count == 0) throw new ArgumentException("The image has no mip levels.", nameof(source));

        bool sameSize = source.Width == width && source.Height == height;
        List<byte[]> rgbaMips;
        if (sameSize && source.Mips.Count >= mipCount)
        {
            rgbaMips = [];
            for (int level = 0; level < mipCount; level++)
                rgbaMips.Add(TextureDecoder.ToRgba8(source.Format, source.Mips[level], Math.Max(1, width >> level), Math.Max(1, height >> level), source.IsSnorm));
        }
        else
        {
            byte[] top = TextureDecoder.ToRgba8(source.Format, source.Mips[0], source.Width, source.Height, source.IsSnorm);
            if (!sameSize) top = Resampler.Resize(top, source.Width, source.Height, width, height, srgb);
            rgbaMips = Resampler.Mips(top, width, height, mipCount, srgb);
        }

        List<byte[]> result = [];
        for (int level = 0; level < mipCount; level++)
            result.Add(TextureEncoder.Encode(format, rgbaMips[level], Math.Max(1, width >> level), Math.Max(1, height >> level)));
        return result;
    }
}
