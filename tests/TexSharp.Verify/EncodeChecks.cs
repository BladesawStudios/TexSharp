using TexSharp;

// Encodes a test picture into each format, decodes it with our own decoder and reports how close it came
// (PSNR over all four channels, or over the channels the format keeps).
internal static class EncodeChecks
{
    // Floors are deliberately low: they catch an encoder that writes the wrong thing, not one that is merely not the best.
    private static readonly Dictionary<TextureFormat, double> Floors = new()
    {
        [TextureFormat.Bc1] = 28, [TextureFormat.Bc2] = 28, [TextureFormat.Bc3] = 28, [TextureFormat.Bc4] = 38,
        [TextureFormat.Bc5] = 36, [TextureFormat.Bc7] = 34,
    };

    public static byte[] Picture(int width, int height, bool opaque = false)
    {
        byte[] rgba = new byte[width * height * 4];
        var random = new Random(7);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                double u = x / (double)width, v = y / (double)height;
                bool disc = (u - 0.5) * (u - 0.5) + (v - 0.5) * (v - 0.5) < 0.09;
                rgba[o] = (byte)(255 * u);
                rgba[o + 1] = (byte)(255 * v);
                rgba[o + 2] = (byte)(disc ? 230 : 40 + random.Next(20));
                rgba[o + 3] = (byte)(!opaque && x < width / 4 ? 255 * v : 255);
            }
        return rgba;
    }

    public static double Psnr(byte[] a, byte[] b, int[] channels)
    {
        double sum = 0;
        long count = 0;
        for (int i = 0; i < a.Length; i += 4)
            foreach (int c in channels)
            {
                double d = a[i + c] - b[i + c];
                sum += d * d;
                count++;
            }
        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (sum / count));
    }

    // JPEGs of awkward sizes, for another program to open: Program jpeg <dir>
    public static int WriteJpegs(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (w, h, quality) in new[] { (1, 1, 75), (7, 3, 75), (16, 16, 75), (17, 33, 75), (100, 1, 75), (64, 48, 100), (64, 48, 10) })
        {
            byte[] picture = Picture(w, h);
            File.WriteAllBytes(Path.Combine(dir, $"{w}x{h}_q{quality}.jpg"), JpegWriter.Encode(picture, w, h, quality));
        }
        return 0;
    }

    public static int Run()
    {
        int w = 96, h = 80, failures = 0;
        byte[] source = Picture(w, h);
        byte[] opaque = Picture(w, h, opaque: true);
        foreach (TextureFormat format in Enum.GetValues<TextureFormat>())
        {
            if (TextureEncoder.WhyNotEncodable(format) is not null) continue;
            int[] channels = format switch
            {
                TextureFormat.Bc4 or TextureFormat.R8 => [0],
                TextureFormat.Bc5 or TextureFormat.RG8 => [0, 1],
                TextureFormat.Rgb565 => [0, 1, 2],
                _ => [0, 1, 2, 3],
            };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // BC1 keeps one bit of alpha, so it is judged on a picture without any.
            byte[] input = format == TextureFormat.Bc1 ? opaque : source;
            byte[] encoded = TextureEncoder.Encode(format, input, w, h);
            long ms = timer.ElapsedMilliseconds;
            byte[] decoded = TextureDecoder.ToRgba8(format, encoded, w, h);
            double psnr = Psnr(input, decoded, channels);
            double floor = Floors.TryGetValue(format, out double f) ? f : format.IsAstc() ? 26 : 20;
            bool ok = encoded.Length == format.LevelSize(w, h) && psnr >= floor;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {format,-10} {psnr,6:F1} dB  (floor {floor})  {ms} ms");
        }

        // Odd sizes (blocks hanging over the edge), with a picture that is one colour ramp so any block can hold it.
        foreach (TextureFormat format in new[] { TextureFormat.Bc1, TextureFormat.Bc7, TextureFormat.Astc4x4, TextureFormat.Astc6x6, TextureFormat.Astc12x12 })
        {
            byte[] small = new byte[13 * 7 * 4];
            for (int i = 0; i < 13 * 7; i++)
            {
                int x = i % 13;
                small[i * 4] = (byte)(x * 19);
                small[i * 4 + 1] = (byte)(30 + x * 8);
                small[i * 4 + 2] = (byte)(240 - x * 19);
                small[i * 4 + 3] = 255;
            }
            byte[] decoded = TextureDecoder.ToRgba8(format, TextureEncoder.Encode(format, small, 13, 7), 13, 7);
            double psnr = Psnr(small, decoded, [0, 1, 2]);
            bool ok = psnr > 30;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {format,-10} 13x7 {psnr,6:F1} dB");
        }

        byte[] flat = Enumerable.Range(0, 16 * 16 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 100)).ToArray();
        foreach (TextureFormat format in Enum.GetValues<TextureFormat>().Where(f => TextureEncoder.WhyNotEncodable(f) is null))
        {
            byte[] back = TextureDecoder.ToRgba8(format, TextureEncoder.Encode(format, flat, 16, 16), 16, 16);
            bool ok = format is TextureFormat.R8 or TextureFormat.RG8 or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Rgb565 or TextureFormat.Rgba4 or TextureFormat.Rgb10A2
                || Psnr(flat, back, [0, 1, 2, 3]) > 35;
            if (!ok) { failures++; Console.WriteLine($"FAIL flat {format}"); }
        }

        Console.WriteLine(failures == 0 ? "encode checks passed" : $"{failures} failures");
        return failures == 0 ? 0 : 2;
    }
}
