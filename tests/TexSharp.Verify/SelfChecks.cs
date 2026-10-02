using TexSharp;

// Checks that need no reference decoder.
internal static class SelfChecks
{
    private static int _failures;

    public static int Run()
    {
        // The small formats must survive expand then collapse for every possible pixel.
        foreach (var format in new[] { TextureFormat.Rgb565, TextureFormat.Rgba4 })
        {
            byte[] all = new byte[65536 * 2];
            for (int v = 0; v < 65536; v++) { all[v * 2] = (byte)v; all[v * 2 + 1] = (byte)(v >> 8); }
            byte[] back = PixelFormats.CollapseRgba8(format, PixelFormats.ExpandToRgba8(format, all, 65536), 65536);
            Check(all.AsSpan().SequenceEqual(back), $"{format} expand/collapse round trip");
        }
        {
            byte[] all = new byte[256];
            for (int v = 0; v < 256; v++) all[v] = (byte)v;
            Check(all.AsSpan().SequenceEqual(PixelFormats.CollapseRgba8(TextureFormat.R8, PixelFormats.ExpandToRgba8(TextureFormat.R8, all, 256), 256)), "R8 round trip");
            byte[] rg = new byte[512];
            Random.Shared.NextBytes(rg);
            Check(rg.AsSpan().SequenceEqual(PixelFormats.CollapseRgba8(TextureFormat.RG8, PixelFormats.ExpandToRgba8(TextureFormat.RG8, rg, 256), 256)), "RG8 round trip");
        }

        // BNTX ids: every format DDS can carry maps back to itself.
        foreach (TextureFormat format in Enum.GetValues<TextureFormat>())
        {
            uint id = BntxFormats.ToBntx(format);
            Check(BntxFormats.TryFromBntx(id, out var back, out _, out _) && back == format, $"{format} bntx id 0x{id:X4}");
        }
        Check(BntxFormats.TryFromBntx(0x1A06, out var f, out bool srgb, out _) && f == TextureFormat.Bc1 && srgb, "BC1 sRGB id");
        Check(BntxFormats.TryFromBntx(0x2D01, out f, out _, out _) && f == TextureFormat.Astc4x4, "ASTC 4x4 id");
        Check(BntxFormats.TryFromBntx(0x3A06, out f, out srgb, out _) && f == TextureFormat.Astc12x12 && srgb, "ASTC 12x12 sRGB id");
        Check(!BntxFormats.TryFromBntx(0x1801, out _, out _, out _), "unsupported format rejected");

        // Sizes.
        Check(TextureFormat.Bc1.LevelSize(13, 7) == 4 * 2 * 8, "BC1 13x7 size");
        Check(TextureFormat.Astc6x6.LevelSize(13, 7) == 3 * 2 * 16, "ASTC 6x6 13x7 size");
        Check(TextureFormat.Rgba8.LevelSize(13, 7) == 13 * 7 * 4, "RGBA8 13x7 size");
        Check(TextureFormats.AstcFromFootprint(10, 8) == TextureFormat.Astc10x8 && TextureFormats.AstcFromFootprint(7, 7) is null, "ASTC footprint lookup");

        // Reserved blocks.
        byte[] pixels = new byte[64];
        Array.Fill(pixels, (byte)9);
        Bc7Reserved(pixels);
        Check(pixels.All(b => b == 0), "BC7 reserved mode gives zeros");

        // Colour space: a legacy-FourCC DDS can't state one, a DX10 one can.
        {
            var bc1 = new DdsImage(4, 4, TextureFormat.Bc1, [new byte[8]], srgb: true);
            Check(bc1.ColorSpaceKnown, "an in-memory sRGB image states its colour space");
            Check(!DdsImage.Parse(bc1.ToBytes()).ColorSpaceKnown, "a BC1 DDS file can't state a colour space");
            var bc7 = new DdsImage(4, 4, TextureFormat.Bc7, [new byte[16]], srgb: true);
            DdsImage parsed = DdsImage.Parse(bc7.ToBytes());
            Check(parsed.ColorSpaceKnown && parsed.IsSrgb, "a BC7 DX10 DDS keeps sRGB");
            Check(!new DdsImage(4, 4, TextureFormat.Bc1, [new byte[8]]).ColorSpaceKnown, "a plain BC1 image doesn't state one");
        }

        // Channel maps and PNG.
        Check(ChannelMap.FromTxtg(0, 1, 2, 3).IsIdentity, "TXTG selectors 0 1 2 3 are the identity");
        Check(ChannelMap.FromTxtg(0, 0, 0, 1) == new ChannelMap(ChannelSource.Red, ChannelSource.Red, ChannelSource.Red, ChannelSource.Green), "TXTG selectors 0 0 0 1");
        Check(ChannelMap.FromTxtg(0, 1, 4, 5) == new ChannelMap(ChannelSource.Red, ChannelSource.Green, ChannelSource.Zero, ChannelSource.One), "TXTG zero and one selectors");
        Check(ChannelMap.FromBntx(2, 3, 4, 5).IsIdentity && ChannelMap.FromBntx(9, 3, 4, 5).Red == ChannelSource.Red, "BNTX channel values, and an unknown one reading red");
        Check(ChannelMap.FromTxtg(0, 1, 4, 5).IsTangentNormal && !ChannelMap.Identity.IsTangentNormal, "tangent-space normal signature");
        byte[] png = PngWriter.Encode(new byte[4 * 3 * 2], 3, 2);
        Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Check(Throws<ArgumentException>(() => PngWriter.Encode(new byte[3], 2, 2)), "short PNG pixels rejected");

        // Bad input.
        Check(Throws<ArgumentException>(() => TextureDecoder.ToRgba8(TextureFormat.Bc1, new byte[7], 4, 4)), "short payload rejected");
        Check(Throws<InvalidDataException>(() => DdsImage.Parse("DDS "u8)), "truncated DDS rejected");
        Check(Throws<InvalidDataException>(() => DdsImage.Parse(new byte[200])), "non-DDS rejected");

        Console.WriteLine(_failures == 0 ? "self checks passed" : $"{_failures} self checks failed");
        return _failures == 0 ? 0 : 2;
    }

    private static void Bc7Reserved(byte[] pixels)
        => TextureDecoder.ToRgba8(TextureFormat.Bc7, new byte[16], 4, 4).CopyTo(pixels, 0);

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) { _failures++; Console.WriteLine("FAIL " + what); }
    }
}
