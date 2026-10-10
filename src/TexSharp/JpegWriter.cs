namespace TexSharp;

/// <summary>
/// Writes an RGBA8 picture as a baseline JPEG (JFIF, 4:2:0, the standard Huffman tables). Alpha is dropped, which is what
/// converting RGBA to RGB does in most image libraries; it is the colour under it that is kept.
/// </summary>
public static class JpegWriter
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // Annex K of the JPEG standard, in natural (not zig-zag) order.
    private static readonly byte[] LuminanceQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChrominanceQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] DcLuminanceBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrominanceBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLuminanceBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static readonly byte[] AcLuminanceValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private static readonly byte[] AcChrominanceBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChrominanceValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    // Where each natural-order coefficient lands in the zig-zag order.
    private static readonly int[] ZigZagPosition = BuildPositions();

    private static int[] BuildPositions()
    {
        int[] positions = new int[64];
        for (int i = 0; i < 64; i++) positions[ZigZag[i]] = i;
        return positions;
    }

    // Cosines for the 8-point DCT: Cos[u, x] = c(u) / 2 * cos((2x + 1) u pi / 16).
    private static readonly float[,] Cos = BuildCos();

    private static float[,] BuildCos()
    {
        float[,] table = new float[8, 8];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                table[u, x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * 0.5 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return table;
    }

    private sealed class HuffmanCodes
    {
        public readonly ushort[] Code = new ushort[256];
        public readonly byte[] Length = new byte[256];

        public static HuffmanCodes Build(byte[] bits, byte[] values)
        {
            var codes = new HuffmanCodes();
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < bits[length - 1]; i++, k++)
                {
                    codes.Code[values[k]] = (ushort)code++;
                    codes.Length[values[k]] = (byte)length;
                }
                code <<= 1;
            }
            return codes;
        }
    }

    private sealed class BitSink
    {
        private readonly List<byte> _bytes = new(1 << 16);
        private uint _accumulator;
        private int _count;

        public List<byte> Bytes => _bytes;

        public void Write(int value, int length)
        {
            if (length == 0) return;
            _accumulator = (_accumulator << length) | ((uint)value & ((1u << length) - 1));
            _count += length;
            while (_count >= 8)
            {
                byte b = (byte)(_accumulator >> (_count - 8));
                _bytes.Add(b);
                if (b == 0xFF) _bytes.Add(0);
                _count -= 8;
            }
        }

        public void Flush()
        {
            if (_count > 0) Write(0x7F, 8 - _count);
        }
    }

    /// <param name="rgba">width * height * 4 bytes, R G B A.</param>
    /// <param name="quality">1 to 100, as in libjpeg; 75 is the usual default.</param>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality = 75)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 65535);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 65535);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"{width}x{height} needs {width * height * 4} bytes of RGBA, got {rgba.Length}.", nameof(rgba));

        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        int[] lumaQuant = Scale(LuminanceQuant, scale), chromaQuant = Scale(ChrominanceQuant, scale);

        // Planes padded to whole 16x16 groups of pixels by repeating the last row and column.
        int mcusX = (width + 15) / 16, mcusY = (height + 15) / 16;
        int paddedWidth = mcusX * 16, paddedHeight = mcusY * 16;
        float[] y = new float[paddedWidth * paddedHeight];
        float[] cb = new float[paddedWidth * paddedHeight];
        float[] cr = new float[paddedWidth * paddedHeight];
        for (int row = 0; row < paddedHeight; row++)
        {
            int sy = Math.Min(row, height - 1);
            for (int col = 0; col < paddedWidth; col++)
            {
                int o = (sy * width + Math.Min(col, width - 1)) * 4;
                float r = rgba[o], g = rgba[o + 1], b = rgba[o + 2];
                int p = row * paddedWidth + col;
                y[p] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                cb[p] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                cr[p] = 0.5f * r - 0.418688f * g - 0.081312f * b;
            }
        }

        List<byte> output = new(width * height / 4 + 1024);
        output.AddRange([0xFF, 0xD8]);
        // JFIF APP0.
        output.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        AddQuantTable(output, 0, lumaQuant);
        AddQuantTable(output, 1, chromaQuant);
        // Baseline frame, three components: Y at 2x2, Cb and Cr at 1x1.
        output.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        AddHuffmanTable(output, 0x00, DcLuminanceBits, DcValues);
        AddHuffmanTable(output, 0x10, AcLuminanceBits, AcLuminanceValues);
        AddHuffmanTable(output, 0x01, DcChrominanceBits, DcValues);
        AddHuffmanTable(output, 0x11, AcChrominanceBits, AcChrominanceValues);
        output.AddRange([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);

        HuffmanCodes dcLuma = HuffmanCodes.Build(DcLuminanceBits, DcValues), dcChroma = HuffmanCodes.Build(DcChrominanceBits, DcValues);
        HuffmanCodes acLuma = HuffmanCodes.Build(AcLuminanceBits, AcLuminanceValues), acChroma = HuffmanCodes.Build(AcChrominanceBits, AcChrominanceValues);

        BitSink sink = new();
        int previousY = 0, previousCb = 0, previousCr = 0;
        float[] block = new float[64];
        int[] quantized = new int[64];
        float[] half = new float[16 * 16];

        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                for (int by = 0; by < 2; by++)
                    for (int bx = 0; bx < 2; bx++)
                    {
                        CopyBlock(y, paddedWidth, mx * 16 + bx * 8, my * 16 + by * 8, block);
                        EncodeBlock(block, lumaQuant, quantized);
                        WriteBlock(sink, quantized, ref previousY, dcLuma, acLuma);
                    }

                Downsample(cb, paddedWidth, mx * 16, my * 16, half);
                EncodeBlock(half, chromaQuant, quantized);
                WriteBlock(sink, quantized, ref previousCb, dcChroma, acChroma);

                Downsample(cr, paddedWidth, mx * 16, my * 16, half);
                EncodeBlock(half, chromaQuant, quantized);
                WriteBlock(sink, quantized, ref previousCr, dcChroma, acChroma);
            }
        }

        sink.Flush();
        output.AddRange(sink.Bytes);
        output.AddRange([0xFF, 0xD9]);
        return [.. output];
    }

    private static int[] Scale(byte[] table, int scale)
    {
        int[] scaled = new int[64];
        for (int i = 0; i < 64; i++) scaled[i] = Math.Clamp((table[i] * scale + 50) / 100, 1, 255);
        return scaled;
    }

    private static void AddQuantTable(List<byte> output, int id, int[] table)
    {
        output.AddRange([0xFF, 0xDB, 0x00, 0x43, (byte)id]);
        for (int i = 0; i < 64; i++) output.Add((byte)table[ZigZag[i]]);
    }

    private static void AddHuffmanTable(List<byte> output, byte classAndId, byte[] bits, byte[] values)
    {
        int length = 2 + 1 + 16 + values.Length;
        output.AddRange([0xFF, 0xC4, (byte)(length >> 8), (byte)length, classAndId]);
        output.AddRange(bits);
        output.AddRange(values);
    }

    private static void CopyBlock(float[] plane, int stride, int x0, int y0, float[] block)
    {
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++) block[row * 8 + col] = plane[(y0 + row) * stride + x0 + col];
    }

    // Averages each 2x2 group of a 16x16 area into an 8x8 block (4:2:0).
    private static void Downsample(float[] plane, int stride, int x0, int y0, float[] output)
    {
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
            {
                int p = (y0 + row * 2) * stride + x0 + col * 2;
                output[row * 8 + col] = (plane[p] + plane[p + 1] + plane[p + stride] + plane[p + stride + 1]) * 0.25f;
            }
    }

    // Forward DCT of an 8x8 block of samples, then division by the quantisation table (rounded), in zig-zag order.
    private static void EncodeBlock(float[] block, int[] quant, int[] zigzag)
    {
        Span<float> temp = stackalloc float[64];
        for (int row = 0; row < 8; row++)
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int x = 0; x < 8; x++) sum += block[row * 8 + x] * Cos[u, x];
                temp[row * 8 + u] = sum;
            }

        for (int u = 0; u < 8; u++)
            for (int v = 0; v < 8; v++)
            {
                float sum = 0;
                for (int row = 0; row < 8; row++) sum += temp[row * 8 + u] * Cos[v, row];
                int index = v * 8 + u;
                zigzag[ZigZagPosition[index]] = (int)MathF.Round(sum / quant[index]);
            }
    }

    private static int BitLength(int value)
    {
        value = Math.Abs(value);
        int length = 0;
        while (value != 0) { length++; value >>= 1; }
        return length;
    }

    private static void WriteBlock(BitSink sink, int[] coefficients, ref int previousDc, HuffmanCodes dc, HuffmanCodes ac)
    {
        int diff = coefficients[0] - previousDc;
        previousDc = coefficients[0];
        int size = BitLength(diff);
        sink.Write(dc.Code[size], dc.Length[size]);
        sink.Write(diff < 0 ? diff - 1 : diff, size);

        int zeros = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = coefficients[i];
            if (value == 0) { zeros++; continue; }

            while (zeros > 15)
            {
                sink.Write(ac.Code[0xF0], ac.Length[0xF0]);
                zeros -= 16;
            }
            int bits = BitLength(value);
            int symbol = (zeros << 4) | bits;
            sink.Write(ac.Code[symbol], ac.Length[symbol]);
            sink.Write(value < 0 ? value - 1 : value, bits);
            zeros = 0;
        }
        if (zeros > 0) sink.Write(ac.Code[0x00], ac.Length[0x00]);
    }
}
