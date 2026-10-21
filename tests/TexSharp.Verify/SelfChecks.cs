using TexSharp;

/// <summary>The verify checks that need no reference decoder.</summary>
/// <remarks>
/// They round-trip the small packed formats over every possible pixel, compare the wide, float and depth formats with
/// pixels worked out by hand, send every format through DDS and back, and check the BNTX ids. Run counts the failures.
/// </remarks>
internal static class SelfChecks
{
    private static int _failures;

    public static int Run()
    {
        // The small formats must survive expand then collapse for every possible pixel.
        foreach (var format in new[] { TextureFormat.Rgb565, TextureFormat.Rgba4, TextureFormat.Bgr565, TextureFormat.Abgr4, TextureFormat.Rgb5A1, TextureFormat.A1Bgr5, TextureFormat.Bgr5A1 })
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
            Check(all.AsSpan().SequenceEqual(PixelFormats.CollapseRgba8(TextureFormat.Rg4, PixelFormats.ExpandToRgba8(TextureFormat.Rg4, all, 256), 256)), "RG4 round trip");
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
        Check(!BntxFormats.TryFromBntx(0x2301, out _, out _, out _), "unsupported format (ETC1) rejected");
        BntxId(0x0A01, TextureFormat.R16, false);
        BntxId(0x0A02, TextureFormat.R16, true);
        BntxId(0x0A03, TextureFormat.R16, false);
        BntxId(0x0A04, TextureFormat.R16, true);
        BntxId(0x0A05, TextureFormat.R16Float, false);
        BntxId(0x0902, TextureFormat.RG8, true);
        BntxId(0x0801, TextureFormat.Bgr565, false);
        BntxId(0x0401, TextureFormat.Abgr4, false);
        BntxId(0x3B01, TextureFormat.Bgr5A1, false);
        BntxId(0x0F05, TextureFormat.R11G11B10Float, false);
        BntxId(0x1205, TextureFormat.RG16Float, false);
        BntxId(0x1301, TextureFormat.D24S8, false);
        BntxId(0x1403, TextureFormat.R32, false);
        BntxId(0x1405, TextureFormat.R32Float, false);
        BntxId(0x1505, TextureFormat.Rgba16Float, false); // TotK's vertex animation textures
        BntxId(0x1605, TextureFormat.D32FloatS8X24, false);
        BntxId(0x1905, TextureFormat.Rgba32Float, false);
        Check(!BntxFormats.TryFromBntx(0x1101, out _, out _, out _), "layout 0x11 is not read");
        BntxId(0x1F05, TextureFormat.Bc6, true);
        BntxId(0x1F0A, TextureFormat.Bc6, false);
        Check(BntxFormats.ToBntx(TextureFormat.Bc6) == 0x1F0A && BntxFormats.ToBntx(TextureFormat.Bc6, snorm: true) == 0x1F05, "BC6 ids are UFLOAT and FLOAT");
        Check(BntxFormats.ToBntx(TextureFormat.R16) == 0x0A01 && BntxFormats.ToBntx(TextureFormat.R16, snorm: true) == 0x0A02, "R16 ids");
        Check(BntxFormats.ToBntx(TextureFormat.R16Float) == 0x0A05 && BntxFormats.ToBntx(TextureFormat.R32) == 0x1403, "R16 float and R32 uint ids");

        // The formats past 8 bits a channel and the other small packed ones, against pixels worked out by hand.
        Decodes(TextureFormat.R16, U16(0, 65535, 32768, 0x1234), [0, 0, 0, 255, 255, 255, 255, 255, 128, 128, 128, 255, 18, 18, 18, 255], "R16 unorm");
        Decodes(TextureFormat.R16, U16(0x8000, 0x8001, 0, 0x7FFF), [0, 0, 0, 255, 0, 0, 0, 255, 128, 128, 128, 255, 255, 255, 255, 255], "R16 snorm", snorm: true);
        Decodes(TextureFormat.R16Float, U16(0, 0x3C00, 0x3800, 0x4000), [0, 0, 0, 255, 255, 255, 255, 255, 128, 128, 128, 255, 255, 255, 255, 255], "R16 float");
        Decodes(TextureFormat.R16, U16(1000, 2000, 3000, 4000, 5000, 6000), Grey(4, 8, 12, 16, 19, 23), "R16 3x2", width: 3, height: 2);
        Decodes(TextureFormat.RG16, U16(65535, 0), [255, 0, 0, 255], "RG16");
        Decodes(TextureFormat.RG16Float, U16(0xBC00, 0x3800), [0, 128, 0, 255], "RG16 float, negative clamped");
        Decodes(TextureFormat.Rgba16, U16(0, 65535, 32768, 65535), [0, 255, 128, 255], "RGBA16");
        Decodes(TextureFormat.Rgba16Float, U16(0x3C00, 0, 0x3800, 0x3400), [255, 0, 128, 64], "RGBA16 float");
        Decodes(TextureFormat.R32, U32(0xFFFFFFFF, 0x80000000), [255, 255, 255, 255, 128, 128, 128, 255], "R32 uint");
        Decodes(TextureFormat.RG32, U32(0x7FFFFFFF, 0x80000000), [255, 0, 0, 255], "RG32 sint", snorm: true);
        Decodes(TextureFormat.Rgb32, U32(0, 0xFFFFFFFF, 0), [0, 255, 0, 255], "RGB32 uint");
        Decodes(TextureFormat.Rgba32, U32(0, 0, 0xFFFFFFFF, 0xFFFFFFFF), [0, 0, 255, 255], "RGBA32 uint");
        Decodes(TextureFormat.R32Float, F32(0.25f, float.NaN), [64, 64, 64, 255, 0, 0, 0, 255], "R32 float, NaN as zero");
        Decodes(TextureFormat.RG32Float, F32(3f, 0.5f), [255, 128, 0, 255], "RG32 float");
        Decodes(TextureFormat.Rgb32Float, F32(0f, 0.5f, 1f), [0, 128, 255, 255], "RGB32 float");
        Decodes(TextureFormat.Rgba32Float, F32(1f, 0.5f, 0f, 1f), [255, 128, 0, 255], "RGBA32 float");
        // 1.0 is exponent 15 with no mantissa, 0.5 exponent 14. R and G have 6 mantissa bits, B 5.
        Decodes(TextureFormat.R11G11B10Float, U32(0x3C0u | (0x380u << 11) | (0x1E0u << 22)), [255, 128, 255, 255], "R11G11B10 float");
        Decodes(TextureFormat.B10G11R11Float, U32(0x1E0u | (0x380u << 10)), [0, 128, 255, 255], "B10G11R11 float");
        Decodes(TextureFormat.Rgb9E5, U32(256u | (128u << 9) | (16u << 27)), [255, 128, 0, 255], "RGB9E5");
        Decodes(TextureFormat.D24S8, U32(0x80FFFFFF), [255, 128, 0, 255], "D24S8 depth in red, stencil in green");
        Decodes(TextureFormat.D32FloatS8X24, [.. BitConverter.GetBytes(0.5f), 255, 0, 0, 0], [128, 255, 0, 255], "D32 float S8");
        Decodes(TextureFormat.Rg4, [0xF3], [51, 255, 0, 255], "RG4");
        Decodes(TextureFormat.Bgr565, U16(0xF800, 0x001F), [0, 0, 255, 255, 255, 0, 0, 255], "B5G6R5");
        Decodes(TextureFormat.Abgr4, U16(0x1234), [17, 34, 51, 68], "A4B4G4R4");
        Decodes(TextureFormat.Rgb5A1, U16(0x801F, 0x7C00), [255, 0, 0, 255, 0, 0, 255, 0], "R5G5B5A1");
        Decodes(TextureFormat.A1Bgr5, U16(0x0001, 0xF800), [0, 0, 0, 255, 255, 0, 0, 0], "A1B5G5R5");
        Decodes(TextureFormat.Bgr5A1, U16(0x7C00, 0x801F), [255, 0, 0, 0, 0, 0, 255, 255], "B5G5R5A1");
        Decodes(TextureFormat.R8, [0x80, 0x81, 0, 0x7F], [0, 0, 0, 255, 0, 0, 0, 255, 127, 127, 127, 255, 255, 255, 255, 255], "R8 snorm", snorm: true);
        Decodes(TextureFormat.Rgba8, [0x7F, 0x81, 0, 0x7F], [255, 0, 127, 255], "RGBA8 snorm", snorm: true);

        // Every format but B10G11R11 goes to DDS and back as itself, and the signed ones say so.
        foreach (TextureFormat format in Enum.GetValues<TextureFormat>().Where(f => f != TextureFormat.B10G11R11Float))
            DdsRoundTrip(format, false);
        foreach (TextureFormat format in new[] { TextureFormat.R8, TextureFormat.RG8, TextureFormat.Rgba8, TextureFormat.R16, TextureFormat.RG16,
                     TextureFormat.Rgba16, TextureFormat.R32, TextureFormat.RG32, TextureFormat.Rgb32, TextureFormat.Rgba32 })
            DdsRoundTrip(format, true);
        Check(Dxgi(new DdsImage(4, 4, TextureFormat.R16, [new byte[32]])) == 56, "R16 is DXGI R16_UNORM");
        Check(Dxgi(new DdsImage(4, 4, TextureFormat.R16Float, [new byte[32]])) == 54, "R16 float is DXGI R16_FLOAT");
        Check(Dxgi(new DdsImage(4, 4, TextureFormat.R11G11B10Float, [new byte[64]])) == 26, "R11G11B10 is DXGI R11G11B10_FLOAT");
        Check(Throws<NotSupportedException>(() => new DdsImage(4, 4, TextureFormat.B10G11R11Float, [new byte[64]]).ToBytes()), "B10G11R11 has no DDS");

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

    private static void BntxId(uint id, TextureFormat want, bool wantSnorm)
        => Check(BntxFormats.TryFromBntx(id, out var format, out _, out bool snorm) && format == want && snorm == wantSnorm,
                 $"bntx id 0x{id:X4} is {want}{(wantSnorm ? " signed" : "")}");

    private static void Decodes(TextureFormat format, byte[] data, byte[] want, string what, bool snorm = false, int width = 0, int height = 1)
    {
        if (width == 0) width = want.Length / 4;
        byte[] got = TextureDecoder.ToRgba8(format, data, width, height, snorm);
        bool same = got.AsSpan().SequenceEqual(want);
        Check(same, what);
        if (!same) Console.WriteLine($"  got {Convert.ToHexString(got)} want {Convert.ToHexString(want)}");
    }

    private static void DdsRoundTrip(TextureFormat format, bool snorm)
    {
        byte[] top = new byte[format.LevelSize(5, 3)], next = new byte[format.LevelSize(2, 1)];
        Random.Shared.NextBytes(top);
        Random.Shared.NextBytes(next);
        DdsImage back = DdsImage.Parse(new DdsImage(5, 3, format, [top, next], snorm: snorm).ToBytes());
        Check(back.Format == format && back.IsSnorm == snorm && back.Width == 5 && back.Height == 3 && back.Mips.Count == 2
              && back.Mips[0].AsSpan().SequenceEqual(top) && back.Mips[1].AsSpan().SequenceEqual(next), $"{format}{(snorm ? " snorm" : "")} DDS round trip");
    }

    private static uint Dxgi(DdsImage image) => BitConverter.ToUInt32(image.ToBytes(), 128);

    private static byte[] U16(params int[] values) => values.SelectMany(v => BitConverter.GetBytes((ushort)v)).ToArray();

    private static byte[] U32(params uint[] values) => values.SelectMany(v => BitConverter.GetBytes(v)).ToArray();

    private static byte[] F32(params float[] values) => values.SelectMany(v => BitConverter.GetBytes(v)).ToArray();

    private static byte[] Grey(params byte[] values) => values.SelectMany(v => new byte[] { v, v, v, 255 }).ToArray();

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
