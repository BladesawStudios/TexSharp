using TexSharp;

// Swizzle: <dir>/manifest.txt lines "name r g b a normal"; <name>.in is RGBA8, <name>.out what the Python renderer
// produces. "normal" means the BC5 path: a tangent-space map gets its blue channel rebuilt, anything else is swizzled.
// PNG: writes images and their raw pixels to <dir> for a reader on the other side to compare.
internal static class ChannelChecks
{
    public static int Swizzle(string dir)
    {
        int cases = 0, failures = 0;
        foreach (string line in File.ReadLines(Path.Combine(dir, "manifest.txt")))
        {
            string[] p = line.Split(' ');
            var map = ChannelMap.FromBntx(byte.Parse(p[1]), byte.Parse(p[2]), byte.Parse(p[3]), byte.Parse(p[4]));
            bool bc5 = p[5] == "1";
            byte[] pixels = File.ReadAllBytes(Path.Combine(dir, p[0] + ".in"));
            byte[] expected = File.ReadAllBytes(Path.Combine(dir, p[0] + ".out"));

            if (bc5 && map.IsTangentNormal) TextureDecoder.ReconstructNormalZ(pixels);
            else map.Apply(pixels);

            cases++;
            if (!pixels.AsSpan().SequenceEqual(expected) && failures++ < 8)
                Console.WriteLine($"FAIL {p[0]} map {map} bc5={bc5}");
        }
        Console.WriteLine($"swizzle cases {cases}  failures {failures}");
        return failures == 0 ? 0 : 2;
    }

    public static int WritePngs(string dir)
    {
        var rng = new Random(2);
        foreach (var (w, h) in new[] { (1, 1), (3, 5), (64, 64), (257, 31), (1, 400), (333, 2), (512, 512) })
        {
            byte[] rgba = new byte[w * h * 4];
            rng.NextBytes(rgba);
            for (int i = 0; i < rgba.Length / 2; i++) rgba[i] = (byte)(i / 7); // a stretch that compresses
            File.WriteAllBytes(Path.Combine(dir, $"img_{w}x{h}.rgba"), rgba);
            PngWriter.Save(Path.Combine(dir, $"img_{w}x{h}.png"), rgba, w, h);
        }
        Console.WriteLine("wrote pngs");
        return 0;
    }
}
