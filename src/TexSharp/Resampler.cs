namespace TexSharp;

/// <summary>
/// Resizing and mip chains for RGBA8 images. Colour is averaged weighted by alpha, so a transparent pixel's
/// colour doesn't bleed into its neighbours, and for sRGB images the averaging is done in linear light.
/// </summary>
internal static class Resampler
{
    private static readonly float[] SrgbToLinear = BuildSrgbToLinear();

    private static float[] BuildSrgbToLinear()
    {
        float[] table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return table;
    }

    private static float LinearToSrgb(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    private readonly record struct Taps(int[] First, float[][] Weights);

    // For each output position, the first input position and the weights of the triangle filter around it.
    // Shrinking widens the filter so every input pixel counts; enlarging is plain bilinear.
    private static Taps BuildTaps(int sourceSize, int targetSize)
    {
        float scale = sourceSize / (float)targetSize;
        float radius = MathF.Max(1f, scale);
        int[] first = new int[targetSize];
        float[][] weights = new float[targetSize][];

        for (int i = 0; i < targetSize; i++)
        {
            float center = (i + 0.5f) * scale;
            int start = (int)MathF.Floor(center - radius);
            int end = (int)MathF.Ceiling(center + radius);
            float[] w = new float[end - start + 1];
            float sum = 0;
            for (int j = start; j <= end; j++)
            {
                float weight = MathF.Max(0f, 1f - MathF.Abs(j + 0.5f - center) / radius);
                w[j - start] = weight;
                sum += weight;
            }

            if (sum <= 0) { w[Math.Clamp((int)center, start, end) - start] = 1; sum = 1; }
            for (int j = 0; j < w.Length; j++) w[j] /= sum;
            first[i] = start;
            weights[i] = w;
        }

        return new Taps(first, weights);
    }

    public static byte[] Resize(ReadOnlySpan<byte> rgba, int width, int height, int newWidth, int newHeight, bool srgb)
    {
        if (width == newWidth && height == newHeight) return rgba.ToArray();

        // Premultiplied, in linear light for sRGB.
        float[] source = new float[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            float a = rgba[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++)
            {
                float v = srgb ? SrgbToLinear[rgba[i * 4 + c]] : rgba[i * 4 + c] / 255f;
                source[i * 4 + c] = v * a;
            }
            source[i * 4 + 3] = a;
        }

        Taps across = BuildTaps(width, newWidth);
        float[] wide = new float[newWidth * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < newWidth; x++)
            {
                float[] w = across.Weights[x];
                for (int k = 0; k < w.Length; k++)
                {
                    int sx = Math.Clamp(across.First[x] + k, 0, width - 1);
                    for (int c = 0; c < 4; c++) wide[(y * newWidth + x) * 4 + c] += source[(y * width + sx) * 4 + c] * w[k];
                }
            }

        Taps down = BuildTaps(height, newHeight);
        byte[] result = new byte[newWidth * newHeight * 4];
        float[] column = new float[4];
        for (int y = 0; y < newHeight; y++)
            for (int x = 0; x < newWidth; x++)
            {
                Array.Clear(column);
                float[] w = down.Weights[y];
                for (int k = 0; k < w.Length; k++)
                {
                    int sy = Math.Clamp(down.First[y] + k, 0, height - 1);
                    for (int c = 0; c < 4; c++) column[c] += wide[(sy * newWidth + x) * 4 + c] * w[k];
                }

                float a = column[3];
                int o = (y * newWidth + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float v = a > 1e-6f ? column[c] / a : 0f;
                    result[o + c] = (byte)Math.Clamp((int)MathF.Round((srgb ? LinearToSrgb(v) : Math.Clamp(v, 0f, 1f)) * 255f), 0, 255);
                }
                result[o + 3] = (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);
            }

        return result;
    }

    /// <summary>The mip chain below <paramref name="level0"/>, each level made from the one above it. Includes level 0.</summary>
    public static List<byte[]> Mips(byte[] level0, int width, int height, int count, bool srgb)
    {
        List<byte[]> mips = [level0];
        byte[] current = level0;
        int w = width, h = height;
        for (int level = 1; level < count; level++)
        {
            int nw = Math.Max(1, w >> 1), nh = Math.Max(1, h >> 1);
            current = Resize(current, w, h, nw, nh, srgb);
            mips.Add(current);
            (w, h) = (nw, nh);
        }
        return mips;
    }
}
