using TexSharp;

// Files built by python/dds_io.py: every one must parse to the same image, and writing it back out, or writing
// the image the manifest describes, must reproduce the same bytes.
internal static class DdsChecks
{
    public static int Run(string dir)
    {
        int failures = 0, count = 0;
        foreach (string line in File.ReadLines(Path.Combine(dir, "manifest.txt")))
        {
            string[] p = line.Split(' ');
            var format = Enum.Parse<TextureFormat>(p[1]);
            int w = int.Parse(p[2]), h = int.Parse(p[3]), mips = int.Parse(p[4]);
            bool srgb = p[5] == "1", snorm = p[6] == "1";
            byte[] file = File.ReadAllBytes(Path.Combine(dir, p[0] + ".dds"));
            count++;

            string? problem = null;
            try
            {
                DdsImage image = DdsImage.Parse(file);
                if (image.Format != format || image.Width != w || image.Height != h) problem = $"parsed {image.Format} {image.Width}x{image.Height}";
                else if (image.Mips.Count != mips) problem = $"parsed {image.Mips.Count} mips";
                else if (image.IsSrgb != srgb || image.IsSnorm != snorm) problem = $"parsed srgb={image.IsSrgb} snorm={image.IsSnorm}";
                else if (!image.ToBytes().AsSpan().SequenceEqual(file)) problem = "rewriting the parsed image changed the bytes";
                else
                {
                    // Python built the file from the requested flags (the last two name digits), which parsing may drop.
                    bool wantSrgb = p[0].EndsWith("_10"), wantSnorm = p[0].EndsWith("_01");
                    var rebuilt = new DdsImage(w, h, format, image.Mips, wantSrgb, wantSnorm);
                    if (!rebuilt.ToBytes().AsSpan().SequenceEqual(file)) problem = "building from scratch gave different bytes";
                }
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
            }

            if (problem is not null && failures++ < 10) Console.WriteLine($"FAIL {p[0]}: {problem}");
        }

        Console.WriteLine($"dds files {count}  failures {failures}");
        return failures == 0 ? 0 : 2;
    }
}
