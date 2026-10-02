using TexSharp;

// Decoder comparison: Program <cases dir>
// DDS comparison with files from the Python writer: Program dds <dir>
// Checks that need no reference: Program self
// Channel swizzle against the Python renderer: Program swizzle <dir>; PNG files for another reader: Program png <dir>
// Each manifest line is "name format width height"; <name>.in holds the payload and <name>.out the
// expected RGBA8 from a reference decoder.
if (args.Length < 1)
{
    Console.WriteLine("usage: TexSharp.Verify <cases dir>");
    return 1;
}

if (args[0] == "dds") return DdsChecks.Run(args[1]);
if (args[0] == "self") return SelfChecks.Run();
if (args[0] == "swizzle") return ChannelChecks.Swizzle(args[1]);
if (args[0] == "png") return ChannelChecks.WritePngs(args[1]);

string dir = args[0];
Dictionary<string, (int Cases, int Bad, long Pixels, long BadPixels)> stats = [];
List<string> samples = [];

foreach (string line in File.ReadLines(Path.Combine(dir, "manifest.txt")))
{
    string[] p = line.Split(' ');
    var format = Enum.Parse<TextureFormat>(p[1]);
    int w = int.Parse(p[2]), h = int.Parse(p[3]);
    byte[] input = File.ReadAllBytes(Path.Combine(dir, p[0] + ".in"));
    byte[] expected = File.ReadAllBytes(Path.Combine(dir, p[0] + ".out"));

    // A fifth column of "round" means the reference truncates where we round: each channel may be 0 or 1 higher.
    bool roundedUp = p.Length > 4 && p[4] == "round";
    // "lenient" means the reference decodes some blocks the spec calls invalid; we answer those with magenta.
    bool lenient = p.Length > 4 && p[4] == "lenient";

    byte[] actual = TextureDecoder.ToRgba8(format, input, w, h);
    int badPixels = 0;
    for (int i = 0; i < w * h; i++)
    {
        bool same = roundedUp
            ? Enumerable.Range(0, 4).All(c => actual[i * 4 + c] - expected[i * 4 + c] is 0 or 1)
            : actual.AsSpan(i * 4, 4).SequenceEqual(expected.AsSpan(i * 4, 4));
        if (!same && lenient && actual.AsSpan(i * 4, 4).SequenceEqual(new byte[] { 255, 0, 255, 255 })) same = true;
        if (!same)
        {
            if (badPixels++ == 0 && samples.Count < 12)
                samples.Add($"{p[0]} pixel {i}: got {Convert.ToHexString(actual.AsSpan(i * 4, 4))} want {Convert.ToHexString(expected.AsSpan(i * 4, 4))}  block {Convert.ToHexString(BlockAt(format, input, w, i))}");
        }
    }

    string group = p[0].Split('_')[0]; stats.TryGetValue(group, out var s);
    stats[group] = (s.Cases + 1, s.Bad + (badPixels > 0 ? 1 : 0), s.Pixels + w * h, s.BadPixels + badPixels);
}

foreach (var (name, s) in stats.OrderBy(k => k.Key))
    Console.WriteLine($"{name,-6} cases {s.Cases,4}  mismatching {s.Bad,4}  pixels {s.BadPixels}/{s.Pixels}");
foreach (string sample in samples) Console.WriteLine("  " + sample);
return stats.Values.Sum(s => s.Bad) == 0 ? 0 : 2;

static ReadOnlySpan<byte> BlockAt(TextureFormat format, byte[] input, int width, int pixel)
{
    int bw = format.BlockWidth(), bh = format.BlockHeight(), bytes = format.BytesPerBlock();
    int blocksX = (width + bw - 1) / bw;
    int block = (pixel / width / bh) * blocksX + (pixel % width) / bw;
    return input.AsSpan(block * bytes, bytes);
}
