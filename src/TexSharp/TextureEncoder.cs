using System.Buffers.Binary;

namespace TexSharp;

/// <summary>Encodes RGBA8 pixels into the formats <see cref="TextureDecoder"/> reads, so an image can be converted to fit a texture.</summary>
/// <remarks>
/// The block compressors are small and fast rather than exhaustive: the result is close to the source (they are checked
/// by decoding it again) but a dedicated tool will squeeze out more quality. Not supported: BC6H (HDR), signed (snorm)
/// data, RGB9E5 and depth. Float formats get the 8-bit values as 0..1.
/// </remarks>
public static class TextureEncoder
{
    /// <summary>Why <paramref name="format"/> can't be encoded, or null if it can.</summary>
    public static string? WhyNotEncodable(TextureFormat format, bool snorm = false)
    {
        if (format == TextureFormat.Bc6) return "BC6H holds HDR data, which can't be made from an 8-bit image.";
        if (snorm) return "signed (snorm) textures can't be made from an 8-bit image.";
        return WideFormats.Handles(format) ? WideFormats.WhyNotEncodable(format) : null;
    }

    /// <summary>
    /// Returns the data of one mip level: <see cref="TextureFormats.LevelSize"/> bytes. <paramref name="rgba"/> is
    /// width * height * 4 bytes, R G B A. BC4 takes the red channel, BC5 red and green, and the small uncompressed
    /// formats whichever channels they hold.
    /// </summary>
    public static byte[] Encode(TextureFormat format, ReadOnlySpan<byte> rgba, int width, int height, bool snorm = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"{width}x{height} needs {width * height * 4} bytes of RGBA, got {rgba.Length}.", nameof(rgba));
        string? why = WhyNotEncodable(format, snorm);
        if (why is not null) throw new NotSupportedException($"Can't make {format} data: {why}");

        int pixels = width * height;
        switch (format)
        {
            case TextureFormat.Rgba8:
                return rgba[..(pixels * 4)].ToArray();
            case TextureFormat.Bgra8:
            {
                byte[] output = new byte[pixels * 4];
                for (int i = 0; i < output.Length; i += 4)
                {
                    output[i] = rgba[i + 2];
                    output[i + 1] = rgba[i + 1];
                    output[i + 2] = rgba[i];
                    output[i + 3] = rgba[i + 3];
                }
                return output;
            }
            case TextureFormat.Rgb10A2:
            {
                byte[] output = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    uint r = (uint)((rgba[i * 4] * 1023 + 127) / 255), g = (uint)((rgba[i * 4 + 1] * 1023 + 127) / 255);
                    uint b = (uint)((rgba[i * 4 + 2] * 1023 + 127) / 255), a = (uint)((rgba[i * 4 + 3] + 42) / 85);
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), r | (g << 10) | (b << 20) | (Math.Min(a, 3u) << 30));
                }
                return output;
            }
            case TextureFormat.R8 or TextureFormat.RG8 or TextureFormat.Rgb565 or TextureFormat.Rgba4
                or (>= TextureFormat.Rg4 and <= TextureFormat.Bgr5A1):
                return PixelFormats.CollapseRgba8(format, rgba, pixels);
            case var _ when WideFormats.Handles(format):
                return WideFormats.Encode(format, rgba, pixels);
        }

        byte[] result = new byte[format.LevelSize(width, height)];
        if (format.IsAstc())
        {
            AstcDecoder.Encode(rgba, width, height, format.BlockWidth(), format.BlockHeight(), result);
            return result;
        }

        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        int blockBytes = format.BytesPerBlock();
        byte[] source = rgba[..(pixels * 4)].ToArray();
        Parallel.For(0, blocksY, by =>
        {
            Span<byte> px = stackalloc byte[64];
            for (int bx = 0; bx < blocksX; bx++)
            {
                Gather(source, width, height, bx * 4, by * 4, 4, 4, px);
                Span<byte> block = result.AsSpan((by * blocksX + bx) * blockBytes, blockBytes);
                switch (format)
                {
                    case TextureFormat.Bc1: BlockEncoders.Bc1(px, block, allowAlpha: true); break;
                    case TextureFormat.Bc2:
                        BlockEncoders.Bc2Alpha(px, block);
                        BlockEncoders.Bc1(px, block[8..], allowAlpha: false);
                        break;
                    case TextureFormat.Bc3:
                        BlockEncoders.Channel(px, 3, block);
                        BlockEncoders.Bc1(px, block[8..], allowAlpha: false);
                        break;
                    case TextureFormat.Bc4: BlockEncoders.Channel(px, 0, block); break;
                    case TextureFormat.Bc5:
                        BlockEncoders.Channel(px, 0, block);
                        BlockEncoders.Channel(px, 1, block[8..]);
                        break;
                    default: BlockEncoders.Bc7(px, block); break;
                }
            }
        });
        return result;
    }

    // Copies one block of pixels out of the image, repeating the edge pixels where the block hangs over.
    internal static void Gather(ReadOnlySpan<byte> rgba, int width, int height, int x0, int y0, int bw, int bh, Span<byte> block)
    {
        for (int y = 0; y < bh; y++)
        {
            int sy = Math.Min(y0 + y, height - 1);
            for (int x = 0; x < bw; x++)
            {
                int sx = Math.Min(x0 + x, width - 1);
                rgba.Slice((sy * width + sx) * 4, 4).CopyTo(block[((y * bw + x) * 4)..]);
            }
        }
    }
}

internal static class Fit
{
    /// <summary>The mean and the main direction of the spread of <paramref name="n"/> points of <paramref name="dim"/> (at most 4) channels.</summary>
    public static void PrincipalAxis(ReadOnlySpan<float> points, int n, int dim, Span<float> mean, Span<float> axis)
    {
        Span<float> cov = stackalloc float[16];
        mean.Clear();
        cov.Clear();
        for (int i = 0; i < n; i++)
            for (int c = 0; c < dim; c++) mean[c] += points[i * dim + c];
        for (int c = 0; c < dim; c++) mean[c] /= n;

        for (int i = 0; i < n; i++)
            for (int a = 0; a < dim; a++)
                for (int b = 0; b < dim; b++)
                    cov[a * 4 + b] += (points[i * dim + a] - mean[a]) * (points[i * dim + b] - mean[b]);

        for (int c = 0; c < dim; c++) axis[c] = cov[c * 4 + c] + 1e-3f;
        Normalize(axis[..dim]);
        Span<float> next = stackalloc float[4];
        for (int iteration = 0; iteration < 8; iteration++)
        {
            for (int a = 0; a < dim; a++)
            {
                float sum = 0;
                for (int b = 0; b < dim; b++) sum += cov[a * 4 + b] * axis[b];
                next[a] = sum;
            }
            if (!Normalize(next[..dim])) break;
            next[..dim].CopyTo(axis);
        }
    }

    private static bool Normalize(Span<float> v)
    {
        float sum = 0;
        foreach (float x in v) sum += x * x;
        if (sum < 1e-12f) return false;
        float scale = 1f / MathF.Sqrt(sum);
        for (int i = 0; i < v.Length; i++) v[i] *= scale;
        return true;
    }

    /// <summary>The two points along <paramref name="axis"/> through <paramref name="mean"/> that bound the points, clamped to 0..255.</summary>
    public static void Extremes(ReadOnlySpan<float> points, int n, int dim, ReadOnlySpan<float> mean, ReadOnlySpan<float> axis, Span<float> e0, Span<float> e1)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float t = 0;
            for (int c = 0; c < dim; c++) t += (points[i * dim + c] - mean[c]) * axis[c];
            lo = MathF.Min(lo, t);
            hi = MathF.Max(hi, t);
        }
        for (int c = 0; c < dim; c++)
        {
            e0[c] = Math.Clamp(mean[c] + axis[c] * lo, 0f, 255f);
            e1[c] = Math.Clamp(mean[c] + axis[c] * hi, 0f, 255f);
        }
    }

    /// <summary>
    /// Least squares for the endpoints that best give <paramref name="points"/> at the blend positions
    /// <paramref name="t"/> (0 is the first endpoint, 1 the second). False if the positions don't spread out enough to tell.
    /// </summary>
    public static bool Solve(ReadOnlySpan<float> t, ReadOnlySpan<float> points, int n, int dim, Span<float> e0, Span<float> e1)
    {
        float a = 0, b = 0, c = 0;
        for (int i = 0; i < n; i++)
        {
            float u = 1f - t[i];
            a += u * u;
            b += u * t[i];
            c += t[i] * t[i];
        }
        float det = a * c - b * b;
        if (MathF.Abs(det) < 1e-4f) return false;

        for (int ch = 0; ch < dim; ch++)
        {
            float x = 0, y = 0;
            for (int i = 0; i < n; i++)
            {
                x += (1f - t[i]) * points[i * dim + ch];
                y += t[i] * points[i * dim + ch];
            }
            e0[ch] = Math.Clamp((c * x - b * y) / det, 0f, 255f);
            e1[ch] = Math.Clamp((a * y - b * x) / det, 0f, 255f);
        }
        return true;
    }
}

internal struct BitWriter
{
    private ulong _low;
    private ulong _high;
    private int _position;

    public void Write(uint value, int count)
    {
        for (int k = 0; k < count; k++, _position++)
        {
            if (((value >> k) & 1) == 0) continue;
            if (_position < 64) _low |= 1UL << _position;
            else _high |= 1UL << (_position - 64);
        }
    }

    public readonly void CopyTo(Span<byte> block)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(block, _low);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], _high);
    }
}

internal static class BlockEncoders
{
    private static readonly byte[] Quant5 = BuildQuant(5);
    private static readonly byte[] Quant6 = BuildQuant(6);

    private static byte[] BuildQuant(int bits)
    {
        byte[] table = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int q = 0; q < 1 << bits; q++)
            {
                int d = Math.Abs(((q << (8 - bits)) | (q >> (2 * bits - 8))) - v);
                if (d < bestDistance) { bestDistance = d; best = q; }
            }
            table[v] = (byte)best;
        }
        return table;
    }

    private static int Round(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 255f));

    private static ushort Pack565(ReadOnlySpan<float> rgb)
        => (ushort)((Quant5[Round(rgb[0])] << 11) | (Quant6[Round(rgb[1])] << 5) | Quant5[Round(rgb[2])]);

    private static void Palette565(ushort c0, ushort c1, bool fourColor, Span<int> palette)
    {
        for (int k = 0; k < 2; k++)
        {
            int c = k == 0 ? c0 : c1;
            int r = c >> 11, g = (c >> 5) & 0x3F, b = c & 0x1F;
            palette[k * 3] = (r << 3) | (r >> 2);
            palette[k * 3 + 1] = (g << 2) | (g >> 4);
            palette[k * 3 + 2] = (b << 3) | (b >> 2);
        }
        for (int ch = 0; ch < 3; ch++)
        {
            palette[6 + ch] = fourColor ? (2 * palette[ch] + palette[3 + ch]) / 3 : (palette[ch] + palette[3 + ch]) / 2;
            palette[9 + ch] = fourColor ? (palette[ch] + 2 * palette[3 + ch]) / 3 : 0;
        }
    }

    /// <summary>BC1 colour block. With <paramref name="allowAlpha"/>, texels under half alpha become the transparent entry.</summary>
    public static void Bc1(ReadOnlySpan<byte> px, Span<byte> block, bool allowAlpha)
    {
        Span<float> points = stackalloc float[48];
        Span<int> opaqueAt = stackalloc int[16];
        int n = 0;
        bool punch = false;
        for (int i = 0; i < 16; i++)
        {
            if (allowAlpha && px[i * 4 + 3] < 128) { punch = true; continue; }
            opaqueAt[n] = i;
            for (int c = 0; c < 3; c++) points[n * 3 + c] = px[i * 4 + c];
            n++;
        }

        if (n == 0)
        {
            block[..4].Clear();
            block[4..8].Fill(0xFF);
            return;
        }

        Span<float> mean = stackalloc float[3], axis = stackalloc float[3], e0 = stackalloc float[3], e1 = stackalloc float[3];
        Fit.PrincipalAxis(points, n, 3, mean, axis);
        Fit.Extremes(points, n, 3, mean, axis, e0, e1);

        Span<int> palette = stackalloc int[12];
        Span<byte> indices = stackalloc byte[16], bestIndices = stackalloc byte[16];
        Span<float> t = stackalloc float[16];
        long bestError = long.MaxValue;
        ushort bestC0 = 0, bestC1 = 0;

        for (int iteration = 0; iteration < 4; iteration++)
        {
            ushort c0 = Pack565(e0), c1 = Pack565(e1);
            if (punch ? c0 > c1 : c0 < c1) (c0, c1) = (c1, c0);
            bool fourColor = !punch;
            Palette565(c0, c1, fourColor, palette);

            long error = 0;
            int choices = fourColor ? 4 : 3;
            for (int i = 0; i < 16; i++)
            {
                if (punch && px[i * 4 + 3] < 128) { indices[i] = 3; continue; }
                int best = 0, bestDistance = int.MaxValue;
                for (int k = 0; k < choices; k++)
                {
                    int d = 0;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int diff = px[i * 4 + ch] - palette[k * 3 + ch];
                        d += diff * diff;
                    }
                    if (d < bestDistance) { bestDistance = d; best = k; }
                }
                // Equal end colours switch the block to the three-colour reading, where entry 3 is transparent.
                indices[i] = (byte)(c0 == c1 && fourColor ? 0 : best);
                error += c0 == c1 && fourColor ? DistanceTo(px, i, palette, 0) : bestDistance;
            }

            if (error < bestError)
            {
                bestError = error;
                bestC0 = c0;
                bestC1 = c1;
                indices.CopyTo(bestIndices);
            }
            if (error == 0) break;

            for (int k = 0; k < n; k++)
            {
                int index = indices[opaqueAt[k]];
                t[k] = index switch { 0 => 0f, 1 => 1f, 2 => fourColor ? 1f / 3f : 0.5f, _ => 2f / 3f };
            }
            if (!Fit.Solve(t, points, n, 3, e0, e1)) break;
        }

        uint packed = 0;
        for (int i = 0; i < 16; i++) packed |= (uint)bestIndices[i] << (2 * i);
        BinaryPrimitives.WriteUInt16LittleEndian(block, bestC0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], bestC1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], packed);
    }

    private static int DistanceTo(ReadOnlySpan<byte> px, int i, ReadOnlySpan<int> palette, int entry)
    {
        int d = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            int diff = px[i * 4 + ch] - palette[entry * 3 + ch];
            d += diff * diff;
        }
        return d;
    }

    public static void Bc2Alpha(ReadOnlySpan<byte> px, Span<byte> block)
    {
        block[..8].Clear();
        for (int i = 0; i < 16; i++)
            block[i >> 1] |= (byte)(Math.Min(15, (px[i * 4 + 3] + 8) / 17) << ((i & 1) * 4));
    }

    /// <summary>One channel of a 4x4 pixel block as a BC4-style 8-byte block (the alpha of BC3, or BC4 and BC5's channels).</summary>
    public static void Channel(ReadOnlySpan<byte> px, int channel, Span<byte> block)
    {
        Span<int> values = stackalloc int[16];
        int min = 255, max = 0;
        bool hasExtreme = false;
        for (int i = 0; i < 16; i++)
        {
            values[i] = px[i * 4 + channel];
            min = Math.Min(min, values[i]);
            max = Math.Max(max, values[i]);
        }

        Span<int> palette = stackalloc int[8];
        Span<byte> indices = stackalloc byte[16], bestIndices = stackalloc byte[16];
        Span<float> t = stackalloc float[16], pts = stackalloc float[16];
        Span<float> r0 = stackalloc float[1], r1 = stackalloc float[1];
        long bestError = long.MaxValue;
        int bestE0 = 0, bestE1 = 0;

        if (min == max)
        {
            block.Clear();
            block[0] = block[1] = (byte)min;
            return;
        }

        // Mode A: eight levels from e0 down to e1 (e0 > e1). Mode B: six levels plus an exact 0 and 255 (e0 <= e1).
        for (int mode = 0; mode < 2; mode++)
        {
            int lo = 255, hi = 0;
            if (mode == 1)
            {
                for (int i = 0; i < 16; i++)
                {
                    if (values[i] is 0 or 255) { hasExtreme = true; continue; }
                    lo = Math.Min(lo, values[i]);
                    hi = Math.Max(hi, values[i]);
                }
                if (!hasExtreme || lo > hi) break;
            }

            float f0 = mode == 0 ? max : lo, f1 = mode == 0 ? min : hi;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                int e0 = (int)MathF.Round(f0), e1 = (int)MathF.Round(f1);
                if (mode == 0 ? e0 <= e1 : e0 > e1) break;
                BuildChannelPalette(e0, e1, mode == 0, palette);

                long error = 0;
                for (int i = 0; i < 16; i++)
                {
                    int best = 0, bestDistance = int.MaxValue;
                    for (int k = 0; k < 8; k++)
                    {
                        int d = Math.Abs(values[i] - palette[k]);
                        if (d < bestDistance) { bestDistance = d; best = k; }
                    }
                    indices[i] = (byte)best;
                    error += bestDistance * bestDistance;
                }

                if (error < bestError)
                {
                    bestError = error;
                    bestE0 = e0;
                    bestE1 = e1;
                    indices.CopyTo(bestIndices);
                }
                if (error == 0) break;

                // Refine: a blend position per texel from its index, then the endpoints that fit them best.
                for (int i = 0; i < 16; i++)
                {
                    int k = indices[i];
                    t[i] = k switch
                    {
                        0 => 0f,
                        1 => 1f,
                        6 when mode == 1 => 0f,
                        7 when mode == 1 => 1f,
                        _ => (k - 1) / (mode == 0 ? 7f : 5f),
                    };
                    pts[i] = values[i];
                }
                if (mode == 1)
                {
                    // The fixed entries aren't on the endpoint line, so leave their texels out of the fit.
                    int m = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        if (indices[i] >= 6) continue;
                        t[m] = t[i];
                        pts[m++] = pts[i];
                    }
                    if (m < 2 || !Fit.Solve(t, pts, m, 1, r0, r1)) break;
                }
                else if (!Fit.Solve(t, pts, 16, 1, r0, r1)) break;
                f0 = r0[0];
                f1 = r1[0];
            }
        }

        ulong packed = (ulong)(byte)bestE0 | ((ulong)(byte)bestE1 << 8);
        for (int i = 0; i < 16; i++) packed |= (ulong)bestIndices[i] << (16 + 3 * i);
        BinaryPrimitives.WriteUInt64LittleEndian(block, packed);
    }

    private static void BuildChannelPalette(int e0, int e1, bool eightLevels, Span<int> palette)
    {
        palette[0] = e0;
        palette[1] = e1;
        if (eightLevels)
            for (int i = 2; i < 8; i++) palette[i] = ((8 - i) * e0 + (i - 1) * e1) / 7;
        else
        {
            for (int i = 2; i < 6; i++) palette[i] = ((6 - i) * e0 + (i - 1) * e1) / 5;
            palette[6] = 0;
            palette[7] = 255;
        }
    }

    /// <summary>BC7 in mode 6: one subset, 7-bit RGBA endpoints with a shared low bit each, four-bit indices.</summary>
    public static void Bc7(ReadOnlySpan<byte> px, Span<byte> block)
    {
        Span<float> points = stackalloc float[64];
        for (int i = 0; i < 64; i++) points[i] = px[i];

        Span<float> mean = stackalloc float[4], axis = stackalloc float[4], f0 = stackalloc float[4], f1 = stackalloc float[4];
        Fit.PrincipalAxis(points, 16, 4, mean, axis);
        Fit.Extremes(points, 16, 4, mean, axis, f0, f1);

        Span<int> q0 = stackalloc int[4], q1 = stackalloc int[4];
        Span<byte> indices = stackalloc byte[16];
        Span<float> t = stackalloc float[16], d = stackalloc float[4];
        long bestError = long.MaxValue;
        BitWriter best = default;

        for (int iteration = 0; iteration < 4; iteration++)
        {
            int p0 = QuantizeEndpoint(f0, q0), p1 = QuantizeEndpoint(f1, q1);

            float length = 0;
            for (int c = 0; c < 4; c++)
            {
                d[c] = q1[c] - q0[c];
                length += d[c] * d[c];
            }

            long error = 0;
            for (int i = 0; i < 16; i++)
            {
                float u = 0;
                if (length > 0)
                    for (int c = 0; c < 4; c++) u += (px[i * 4 + c] - q0[c]) * d[c];
                float target = length > 0 ? Math.Clamp(u / length, 0f, 1f) * 64f : 0f;

                int bestIndex = 0;
                float bestDistance = float.MaxValue;
                for (int k = 0; k < 16; k++)
                {
                    float diff = Math.Abs(Bc7Decoder.Weights[4][k] - target);
                    if (diff < bestDistance) { bestDistance = diff; bestIndex = k; }
                }
                indices[i] = (byte)bestIndex;
            }

            // The anchor texel's index loses its top bit: if it is high, swap the endpoints and mirror the indices.
            if (indices[0] >= 8)
            {
                for (int c = 0; c < 4; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
                (p0, p1) = (p1, p0);
                for (int i = 0; i < 16; i++) indices[i] = (byte)(15 - indices[i]);
            }

            for (int i = 0; i < 16; i++)
            {
                int w = Bc7Decoder.Weights[4][indices[i]];
                for (int c = 0; c < 4; c++)
                {
                    int diff = ((q0[c] * (64 - w) + q1[c] * w + 32) >> 6) - px[i * 4 + c];
                    error += diff * diff;
                }
            }

            if (error < bestError)
            {
                bestError = error;
                BitWriter writer = default;
                writer.Write(0x40, 7);
                for (int c = 0; c < 4; c++)
                {
                    writer.Write((uint)(q0[c] >> 1), 7);
                    writer.Write((uint)(q1[c] >> 1), 7);
                }
                writer.Write((uint)p0, 1);
                writer.Write((uint)p1, 1);
                for (int i = 0; i < 16; i++) writer.Write(indices[i], i == 0 ? 3 : 4);
                best = writer;
            }
            if (error == 0) break;

            for (int i = 0; i < 16; i++) t[i] = Bc7Decoder.Weights[4][indices[i]] / 64f;
            if (!Fit.Solve(t, points, 16, 4, f0, f1)) break;
        }

        best.CopyTo(block);
    }

    // The 8-bit endpoint closest to the target whose low bit is the shared p-bit; returns the p-bit chosen.
    // Writes the full 8-bit values (what the decoder reconstructs) to <paramref name="q"/>.
    private static int QuantizeEndpoint(ReadOnlySpan<float> target, Span<int> q)
    {
        int bestP = 0;
        float bestError = float.MaxValue;
        Span<int> trial = stackalloc int[4];
        for (int p = 0; p < 2; p++)
        {
            float error = 0;
            for (int c = 0; c < 4; c++)
            {
                int v = ((int)MathF.Round((Math.Clamp(target[c], 0f, 255f) - p) / 2f) << 1) | p;
                v = Math.Clamp(v, p, 254 + p);
                trial[c] = v;
                // Alpha counts for more: a texture that is meant to be fully opaque should stay exactly opaque.
                error += (v - target[c]) * (v - target[c]) * (c == 3 ? 16f : 1f);
            }
            if (error < bestError)
            {
                bestError = error;
                bestP = p;
                trial.CopyTo(q);
            }
        }
        return bestP;
    }
}
