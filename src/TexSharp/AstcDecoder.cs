using System.Buffers.Binary;

namespace TexSharp;

// LDR ASTC decoder, 2D footprints 4x4 to 12x12. Blocks the spec marks invalid, and blocks using HDR modes, decode to the error colour.
internal static class AstcDecoder
{
    private const int MaxTexels = 144;
    private const int GridOffset = 0;
    private const int WeightsOffset = GridOffset + 64;
    private const int ColorValuesOffset = WeightsOffset + MaxTexels * 2;
    private const int ScratchBytes = MaxTexels * 4 + ColorValuesOffset + 32;
    private const int ScratchInts = 36;

    /// <summary>
    /// Decodes <paramref name="blocks"/> (16 bytes per block, blocks in row-major order, as stored in a DDS or a
    /// deswizzled BNTX level) into <paramref name="rgba"/>: width * height * 4 bytes, row-major, R G B A.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> blocks, int width, int height, int blockWidth, int blockHeight, Span<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockWidth, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockWidth, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockHeight, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockHeight, 12);

        int blocksX = (width + blockWidth - 1) / blockWidth;
        int blocksY = (height + blockHeight - 1) / blockHeight;
        if (blocks.Length < (long)blocksX * blocksY * 16)
            throw new ArgumentException("Not enough block data for the given dimensions.", nameof(blocks));
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException("Output buffer is too small for the given dimensions.", nameof(rgba));
        if (width == 0 || height == 0) return;

        Footprint footprint = Footprint.Get(blockWidth, blockHeight);
        Span<byte> scratch = stackalloc byte[ScratchBytes];
        Span<int> ints = stackalloc int[ScratchInts];
        Span<byte> px = scratch[..(blockWidth * blockHeight * 4)];
        Span<byte> work = scratch[(MaxTexels * 4)..];

        int blockIndex = 0;
        for (int by = 0; by < blocksY; by++)
        {
            int y0 = by * blockHeight;
            int rows = Math.Min(blockHeight, height - y0);
            for (int bx = 0; bx < blocksX; bx++, blockIndex++)
            {
                ReadOnlySpan<byte> block = blocks.Slice(blockIndex * 16, 16);
                ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(block);
                ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);

                if (!DecodeBlock(lo, hi, footprint, px, work, ints))
                    FillError(px);

                int x0 = bx * blockWidth;
                int cols = Math.Min(blockWidth, width - x0);
                for (int r = 0; r < rows; r++)
                    px.Slice(r * blockWidth * 4, cols * 4).CopyTo(rgba[(((y0 + r) * width + x0) * 4)..]);
            }
        }
    }

    private static void FillError(Span<byte> px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0xFF;
            px[i + 1] = 0x00;
            px[i + 2] = 0xFF;
            px[i + 3] = 0xFF;
        }
    }

    private static byte To8(int c16) => (byte)((c16 * 255 + 32768) >> 16);

    private static bool DecodeBlock(ulong lo, ulong hi, Footprint fp, Span<byte> px, Span<byte> work, Span<int> ints)
    {
        int bw = fp.Width, bh = fp.Height;
        int blockMode = (int)(lo & 0x7FF);
        if ((blockMode & 0x1FF) == 0x1FC) return DecodeVoidExtent(lo, hi, px);

        // Block mode: weight grid size, precision and plane count
        int a = (blockMode >> 5) & 3;
        int high = (blockMode >> 9) & 1;
        int dualBit = (blockMode >> 10) & 1;
        int rq = (blockMode >> 4) & 1;
        int wx, wy;
        if ((blockMode & 3) != 0)
        {
            rq |= (blockMode & 3) << 1;
            int b = (blockMode >> 7) & 3;
            switch ((blockMode >> 2) & 3)
            {
                case 0: wx = b + 4; wy = a + 2; break;
                case 1: wx = b + 8; wy = a + 2; break;
                case 2: wx = a + 2; wy = b + 8; break;
                default:
                    b &= 1;
                    if ((blockMode & 0x100) != 0) { wx = b + 2; wy = a + 2; }
                    else { wx = a + 2; wy = b + 6; }
                    break;
            }
        }
        else
        {
            int rr = (blockMode >> 2) & 3;
            if (rr == 0) return false;
            rq |= rr << 1;
            int b = (blockMode >> 9) & 3;
            switch ((blockMode >> 7) & 3)
            {
                case 0: wx = 12; wy = a + 2; break;
                case 1: wx = a + 2; wy = 12; break;
                case 2: wx = a + 6; wy = b + 6; high = 0; dualBit = 0; break;
                default:
                    switch (a)
                    {
                        case 0: wx = 6; wy = 10; break;
                        case 1: wx = 10; wy = 6; break;
                        default: return false;
                    }
                    break;
            }
        }

        bool dual = dualBit != 0;
        int planes = dual ? 2 : 1;
        int weightCount = wx * wy * planes;
        if (wx > bw || wy > bh || weightCount > 64) return false;
        int weightRange = high * 6 + rq - 2;
        int weightBits = Tables.IseBits(weightCount, weightRange);
        if (weightBits < 24 || weightBits > 96) return false;

        int parts = (int)((lo >> 11) & 3) + 1;
        if (dual && parts == 4) return false;

        // Colour endpoint modes
        Span<int> cems = ints[32..36];
        int colorStart;
        int extraCemBits = 0;
        if (parts == 1)
        {
            cems[0] = (int)((lo >> 13) & 0xF);
            colorStart = 17;
        }
        else
        {
            colorStart = 29;
            int baseClass = (int)((lo >> 23) & 3);
            if (baseClass == 0)
            {
                int cem = (int)((lo >> 25) & 0xF);
                for (int i = 0; i < parts; i++) cems[i] = cem;
            }
            else
            {
                extraCemBits = 3 * parts - 4;
                int encoded = (int)((lo >> 23) & 0x3F)
                    | (int)(Tables.GetBits(lo, hi, 128 - weightBits - extraCemBits, extraCemBits) << 6);
                baseClass--;
                for (int i = 0; i < parts; i++)
                {
                    int cls = baseClass + ((encoded >> (2 + i)) & 1);
                    cems[i] = cls * 4 + ((encoded >> (2 + parts + 2 * i)) & 3);
                }
            }
        }

        int ccs = 0;
        int colorEnd = 128 - weightBits - extraCemBits;
        if (dual)
        {
            colorEnd -= 2;
            ccs = (int)Tables.GetBits(lo, hi, colorEnd, 2);
        }

        int valueCount = 0;
        for (int i = 0; i < parts; i++)
        {
            int cem = cems[i];
            if (cem is 2 or 3 or 7 or 11 or 14 or 15) return false;
            valueCount += (cem >> 2) + 1;
        }
        valueCount *= 2;
        if (valueCount > 18) return false;

        int colorRange = -1;
        for (int r = Tables.RangeCount - 1; r >= 4; r--)
        {
            if (colorStart + Tables.IseBits(valueCount, r) <= colorEnd)
            {
                colorRange = r;
                break;
            }
        }
        if (colorRange < 0) return false;

        // Endpoints
        Span<byte> colorValues = work[ColorValuesOffset..];
        Tables.DecodeIse(lo, hi, colorStart, valueCount, colorRange, colorValues);
        ReadOnlySpan<byte> colorUnquant = Tables.ColorUnquant.AsSpan(colorRange * 256, 256);
        for (int i = 0; i < valueCount; i++) colorValues[i] = colorUnquant[colorValues[i]];

        Span<int> e0 = ints[..16];
        Span<int> e1 = ints[16..32];
        int valuePos = 0;
        for (int i = 0; i < parts; i++)
        {
            int n = ((cems[i] >> 2) + 1) * 2;
            DecodeEndpoints(cems[i], colorValues.Slice(valuePos, n), e0.Slice(i * 4, 4), e1.Slice(i * 4, 4));
            valuePos += n;
        }

        // Weights are stored backwards from the end of the block
        Span<byte> grid = work.Slice(GridOffset, 64);
        Tables.DecodeIse(ReverseBits(hi), ReverseBits(lo), 0, weightCount, weightRange, grid);
        ReadOnlySpan<byte> weightUnquant = Tables.WeightUnquant.AsSpan(weightRange * 32, 32);
        for (int i = 0; i < weightCount; i++) grid[i] = weightUnquant[grid[i]];

        ReadOnlySpan<byte> partitionMap = parts > 1 ? fp.GetPartitionMap((int)((lo >> 13) & 0x3FF), parts) : default;
        Footprint.Infill infill = fp.GetInfill(wx, wy);
        byte[] index = infill.Index;
        byte[] factor = infill.Weight;

        // Endpoints become a 16-bit base (with the rounding term folded in) and a per-weight step
        for (int k = 0; k < 16; k++)
        {
            int start = e0[k];
            e1[k] = (e1[k] - start) * 257;
            e0[k] = start * (257 * 64) + 32;
        }

        int texels = bw * bh;
        Span<byte> weights0 = work.Slice(WeightsOffset, texels);
        Span<byte> weights1 = weights0;
        Interpolate(index, factor, grid, planes, 0, weights0);
        if (dual)
        {
            weights1 = work.Slice(WeightsOffset + MaxTexels, texels);
            Interpolate(index, factor, grid, planes, 1, weights1);
        }

        ReadOnlySpan<byte> wr = ccs == 0 ? weights1 : weights0;
        ReadOnlySpan<byte> wg = ccs == 1 ? weights1 : weights0;
        ReadOnlySpan<byte> wb = ccs == 2 ? weights1 : weights0;
        ReadOnlySpan<byte> wa = ccs == 3 ? weights1 : weights0;
        if (parts == 1)
        {
            int r0 = e0[0], g0 = e0[1], b0 = e0[2], a0 = e0[3];
            int rd = e1[0], gd = e1[1], bd = e1[2], ad = e1[3];
            for (int p = 0, o = 0; p < texels; p++, o += 4)
            {
                px[o] = To8((r0 + rd * wr[p]) >> 6);
                px[o + 1] = To8((g0 + gd * wg[p]) >> 6);
                px[o + 2] = To8((b0 + bd * wb[p]) >> 6);
                px[o + 3] = To8((a0 + ad * wa[p]) >> 6);
            }
        }
        else
        {
            for (int p = 0, o = 0; p < texels; p++, o += 4)
            {
                int c = partitionMap[p] * 4;
                px[o] = To8((e0[c] + e1[c] * wr[p]) >> 6);
                px[o + 1] = To8((e0[c + 1] + e1[c + 1] * wg[p]) >> 6);
                px[o + 2] = To8((e0[c + 2] + e1[c + 2] * wb[p]) >> 6);
                px[o + 3] = To8((e0[c + 3] + e1[c + 3] * wa[p]) >> 6);
            }
        }
        return true;
    }

    // Weight infill: bilinear blend of the four surrounding grid weights, in sixteenths.
    private static void Interpolate(byte[] index, byte[] factor, ReadOnlySpan<byte> grid, int stride, int plane, Span<byte> weights)
    {
        if (stride == 1)
        {
            for (int p = 0, k = 0; p < weights.Length; p++, k += 4)
            {
                int sum = grid[index[k]] * factor[k] + grid[index[k + 1]] * factor[k + 1]
                    + grid[index[k + 2]] * factor[k + 2] + grid[index[k + 3]] * factor[k + 3];
                weights[p] = (byte)((sum + 8) >> 4);
            }
            return;
        }

        for (int p = 0, k = 0; p < weights.Length; p++, k += 4)
        {
            int sum = grid[index[k] * 2 + plane] * factor[k] + grid[index[k + 1] * 2 + plane] * factor[k + 1]
                + grid[index[k + 2] * 2 + plane] * factor[k + 2] + grid[index[k + 3] * 2 + plane] * factor[k + 3];
            weights[p] = (byte)((sum + 8) >> 4);
        }
    }

    private static bool DecodeVoidExtent(ulong lo, ulong hi, Span<byte> px)
    {
        if (((lo >> 9) & 1) != 0) return false;

        ulong sMin = (lo >> 12) & 0x1FFF, sMax = (lo >> 25) & 0x1FFF;
        ulong tMin = (lo >> 38) & 0x1FFF, tMax = (lo >> 51) & 0x1FFF;
        bool noExtent = sMin == 0x1FFF && sMax == 0x1FFF && tMin == 0x1FFF && tMax == 0x1FFF;
        if (!noExtent && (sMin >= sMax || tMin >= tMax)) return false;

        // The constant colour is truncated to its high byte; for 8-bit sources (c * 257) this equals To8.
        byte r = (byte)(hi >> 8);
        byte g = (byte)(hi >> 24);
        byte b = (byte)(hi >> 40);
        byte al = (byte)(hi >> 56);
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = al;
        }
        return true;
    }

    // Endpoint values are 0..255 here. Returns the endpoint pair as RGBA in e0/e1.
    private static void DecodeEndpoints(int cem, ReadOnlySpan<byte> v, Span<int> e0, Span<int> e1)
    {
        switch (cem)
        {
            case 0:
                Set(e0, v[0], v[0], v[0], 255);
                Set(e1, v[1], v[1], v[1], 255);
                break;
            case 1:
            {
                int l0 = (v[0] >> 2) | (v[1] & 0xC0);
                int l1 = Math.Min(l0 + (v[1] & 0x3F), 255);
                Set(e0, l0, l0, l0, 255);
                Set(e1, l1, l1, l1, 255);
                break;
            }
            case 4:
                Set(e0, v[0], v[0], v[0], v[2]);
                Set(e1, v[1], v[1], v[1], v[3]);
                break;
            case 5:
            {
                int l0 = v[0], d = v[1];
                TransferSigned(ref d, ref l0);
                int a0 = v[2], ad = v[3];
                TransferSigned(ref ad, ref a0);
                int l1 = Clamp(l0 + d), a1 = Clamp(a0 + ad);
                Set(e0, l0, l0, l0, a0);
                Set(e1, l1, l1, l1, a1);
                break;
            }
            case 6:
                Set(e0, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, 255);
                Set(e1, v[0], v[1], v[2], 255);
                break;
            case 8:
                Direct(v, e0, e1, 255, 255);
                break;
            case 9:
                Delta(v, e0, e1, false);
                break;
            case 10:
                Set(e0, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, v[4]);
                Set(e1, v[0], v[1], v[2], v[5]);
                break;
            case 12:
                Direct(v, e0, e1, v[6], v[7]);
                break;
            default:
                Delta(v, e0, e1, true);
                break;
        }
    }

    private static void Direct(ReadOnlySpan<byte> v, Span<int> e0, Span<int> e1, int alpha0, int alpha1)
    {
        if (v[1] + v[3] + v[5] >= v[0] + v[2] + v[4])
        {
            Set(e0, v[0], v[2], v[4], alpha0);
            Set(e1, v[1], v[3], v[5], alpha1);
        }
        else
        {
            Set(e0, (v[1] + v[5]) >> 1, (v[3] + v[5]) >> 1, v[5], alpha1);
            Set(e1, (v[0] + v[4]) >> 1, (v[2] + v[4]) >> 1, v[4], alpha0);
        }
    }

    private static void Delta(ReadOnlySpan<byte> v, Span<int> e0, Span<int> e1, bool withAlpha)
    {
        int r0 = v[0], g0 = v[2], b0 = v[4], a0 = 255;
        int dr = v[1], dg = v[3], db = v[5], da = 0;
        TransferSigned(ref dr, ref r0);
        TransferSigned(ref dg, ref g0);
        TransferSigned(ref db, ref b0);
        if (withAlpha)
        {
            a0 = v[6];
            da = v[7];
            TransferSigned(ref da, ref a0);
        }

        int r1 = r0 + dr, g1 = g0 + dg, b1 = b0 + db, a1 = withAlpha ? a0 + da : 255;
        if (dr + dg + db >= 0)
        {
            Set(e0, Clamp(r0), Clamp(g0), Clamp(b0), Clamp(a0));
            Set(e1, Clamp(r1), Clamp(g1), Clamp(b1), Clamp(a1));
        }
        else
        {
            Set(e0, Clamp((r1 + b1) >> 1), Clamp((g1 + b1) >> 1), Clamp(b1), Clamp(a1));
            Set(e1, Clamp((r0 + b0) >> 1), Clamp((g0 + b0) >> 1), Clamp(b0), Clamp(a0));
        }
    }

    private static void TransferSigned(ref int a, ref int b)
    {
        b >>= 1;
        b |= a & 0x80;
        a >>= 1;
        a &= 0x3F;
        if ((a & 0x20) != 0) a -= 0x40;
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static void Set(Span<int> e, int r, int g, int b, int a)
    {
        e[0] = r;
        e[1] = g;
        e[2] = b;
        e[3] = a;
    }

    private static ulong ReverseBits(ulong v)
    {
        v = ((v >> 1) & 0x5555555555555555UL) | ((v & 0x5555555555555555UL) << 1);
        v = ((v >> 2) & 0x3333333333333333UL) | ((v & 0x3333333333333333UL) << 2);
        v = ((v >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((v & 0x0F0F0F0F0F0F0F0FUL) << 4);
        return BinaryPrimitives.ReverseEndianness(v);
    }

    // Per-footprint data that only depends on the block size, built on first use. Racing threads may both build an entry, which is harmless.
    private sealed class Footprint
    {
        private static readonly Footprint?[] Cache = new Footprint?[9 * 9];

        private readonly byte[]?[] partitionMaps = new byte[]?[3 * 1024];
        private readonly Infill?[] infills = new Infill?[11 * 11];

        public int Width { get; }
        public int Height { get; }

        private Footprint(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public static Footprint Get(int width, int height)
        {
            int slot = (width - 4) * 9 + (height - 4);
            return Cache[slot] ??= new Footprint(width, height);
        }

        public byte[] GetPartitionMap(int seed, int parts)
        {
            int slot = (parts - 2) * 1024 + seed;
            return partitionMaps[slot] ??= BuildPartitionMap(seed, parts);
        }

        public Infill GetInfill(int gridWidth, int gridHeight)
        {
            int slot = (gridWidth - 2) * 11 + (gridHeight - 2);
            return infills[slot] ??= new Infill(Width, Height, gridWidth, gridHeight);
        }

        private byte[] BuildPartitionMap(int seed, int parts)
        {
            bool small = Width * Height < 31;
            seed += (parts - 1) * 1024;
            uint rnum = Hash52((uint)seed);

            int s1 = (int)(rnum & 0xF), s2 = (int)((rnum >> 4) & 0xF), s3 = (int)((rnum >> 8) & 0xF), s4 = (int)((rnum >> 12) & 0xF);
            int s5 = (int)((rnum >> 16) & 0xF), s6 = (int)((rnum >> 20) & 0xF), s7 = (int)((rnum >> 24) & 0xF), s8 = (int)((rnum >> 28) & 0xF);
            s1 *= s1; s2 *= s2; s3 *= s3; s4 *= s4; s5 *= s5; s6 *= s6; s7 *= s7; s8 *= s8;

            int sh1, sh2;
            if ((seed & 1) != 0)
            {
                sh1 = (seed & 2) != 0 ? 4 : 5;
                sh2 = parts == 3 ? 6 : 5;
            }
            else
            {
                sh1 = parts == 3 ? 6 : 5;
                sh2 = (seed & 2) != 0 ? 4 : 5;
            }
            s1 >>= sh1; s2 >>= sh2; s3 >>= sh1; s4 >>= sh2; s5 >>= sh1; s6 >>= sh2; s7 >>= sh1; s8 >>= sh2;

            int k0 = (int)(rnum >> 14), k1 = (int)(rnum >> 10), k2 = (int)(rnum >> 6), k3 = (int)(rnum >> 2);
            var map = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                int ty = small ? y << 1 : y;
                for (int x = 0; x < Width; x++)
                {
                    int tx = small ? x << 1 : x;
                    int a = (s1 * tx + s2 * ty + k0) & 0x3F;
                    int b = (s3 * tx + s4 * ty + k1) & 0x3F;
                    int c = parts < 3 ? 0 : (s5 * tx + s6 * ty + k2) & 0x3F;
                    int d = parts < 4 ? 0 : (s7 * tx + s8 * ty + k3) & 0x3F;
                    int part;
                    if (a >= b && a >= c && a >= d) part = 0;
                    else if (b >= c && b >= d) part = 1;
                    else if (c >= d) part = 2;
                    else part = 3;
                    map[y * Width + x] = (byte)part;
                }
            }
            return map;
        }

        private static uint Hash52(uint p)
        {
            p ^= p >> 15; p -= p << 17; p += p << 7; p += p << 4;
            p ^= p >> 5; p += p << 16; p ^= p >> 7; p ^= p >> 3;
            p ^= p << 6; p ^= p >> 17;
            return p;
        }

        // For each texel: four grid weight indices and their bilinear factors (sixteenths).
        public sealed class Infill
        {
            public byte[] Index { get; }
            public byte[] Weight { get; }

            public Infill(int bw, int bh, int wx, int wy)
            {
                Index = new byte[bw * bh * 4];
                Weight = new byte[bw * bh * 4];
                int ds = (1024 + bw / 2) / (bw - 1);
                int dt = (1024 + bh / 2) / (bh - 1);
                for (int y = 0; y < bh; y++)
                {
                    int gt = (dt * y * (wy - 1) + 32) >> 6;
                    int jt = gt >> 4, ft = gt & 15;
                    int row0 = Math.Min(jt, wy - 1) * wx;
                    int row1 = Math.Min(jt + 1, wy - 1) * wx;
                    for (int x = 0; x < bw; x++)
                    {
                        int gs = (ds * x * (wx - 1) + 32) >> 6;
                        int js = gs >> 4, fs = gs & 15;
                        int col0 = Math.Min(js, wx - 1);
                        int col1 = Math.Min(js + 1, wx - 1);

                        int w11 = (fs * ft + 8) >> 4;
                        int k = (y * bw + x) * 4;
                        Index[k] = (byte)(row0 + col0);
                        Index[k + 1] = (byte)(row0 + col1);
                        Index[k + 2] = (byte)(row1 + col0);
                        Index[k + 3] = (byte)(row1 + col1);
                        Weight[k] = (byte)(16 - fs - ft + w11);
                        Weight[k + 1] = (byte)(fs - w11);
                        Weight[k + 2] = (byte)(ft - w11);
                        Weight[k + 3] = (byte)w11;
                    }
                }
            }
        }
    }

    // Integer sequence encoding tables, built on first use.
    private static class Tables
    {
        public const int RangeCount = 21;

        private static readonly int[] Levels = [2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256];
        private static readonly int[] ExtraBits = [1, 0, 2, 0, 1, 3, 1, 2, 4, 2, 3, 5, 3, 4, 6, 4, 5, 7, 5, 6, 8];
        // 0: bits only, 1: trits, 2: quints
        private static readonly int[] Kind = [0, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0];

        private static readonly byte[] TritTable = BuildTritTable();
        private static readonly byte[] QuintTable = BuildQuintTable();
        public static readonly byte[] ColorUnquant = BuildColorUnquant();
        public static readonly byte[] WeightUnquant = BuildWeightUnquant();

        public static int IseBits(int count, int range)
        {
            int bits = ExtraBits[range] * count;
            return Kind[range] switch
            {
                1 => bits + (8 * count + 4) / 5,
                2 => bits + (7 * count + 2) / 3,
                _ => bits,
            };
        }

        public static uint GetBits(ulong lo, ulong hi, int pos, int count)
        {
            if (count == 0 || pos >= 128) return 0;
            ulong v;
            if (pos >= 64)
            {
                v = hi >> (pos - 64);
            }
            else
            {
                v = lo >> pos;
                if (pos != 0) v |= hi << (64 - pos);
            }
            return (uint)(v & ((1UL << count) - 1));
        }

        // Writes the quantised index of each value (0 .. levels - 1).
        public static void DecodeIse(ulong lo, ulong hi, int pos, int count, int range, Span<byte> output)
        {
            int m = ExtraBits[range];
            switch (Kind[range])
            {
                case 0:
                    for (int i = 0; i < count; i++)
                    {
                        output[i] = (byte)GetBits(lo, hi, pos, m);
                        pos += m;
                    }
                    break;
                case 1:
                {
                    Span<uint> low = stackalloc uint[5];
                    for (int g = 0; g < count; g += 5)
                    {
                        int n = Math.Min(5, count - g);
                        uint t = 0;
                        for (int i = 0; i < n; i++)
                        {
                            low[i] = GetBits(lo, hi, pos, m);
                            pos += m;
                            int chunk = i is 2 or 4 ? 1 : 2;
                            int shift = i switch { 0 => 0, 1 => 2, 2 => 4, 3 => 5, _ => 7 };
                            t |= GetBits(lo, hi, pos, chunk) << shift;
                            pos += chunk;
                        }
                        for (int i = 0; i < n; i++)
                            output[g + i] = (byte)((TritTable[(int)t * 5 + i] << m) | (int)low[i]);
                    }
                    break;
                }
                default:
                {
                    Span<uint> low = stackalloc uint[3];
                    for (int g = 0; g < count; g += 3)
                    {
                        int n = Math.Min(3, count - g);
                        uint q = 0;
                        for (int i = 0; i < n; i++)
                        {
                            low[i] = GetBits(lo, hi, pos, m);
                            pos += m;
                            int chunk = i == 0 ? 3 : 2;
                            int shift = i switch { 0 => 0, 1 => 3, _ => 5 };
                            q |= GetBits(lo, hi, pos, chunk) << shift;
                            pos += chunk;
                        }
                        for (int i = 0; i < n; i++)
                            output[g + i] = (byte)((QuintTable[(int)q * 3 + i] << m) | (int)low[i]);
                    }
                    break;
                }
            }
        }

        private static byte[] BuildTritTable()
        {
            var table = new byte[256 * 5];
            for (int t = 0; t < 256; t++)
            {
                int c, t4, t3, t2, t1, t0;
                if (((t >> 2) & 7) == 7)
                {
                    c = (((t >> 5) & 7) << 2) | (t & 3);
                    t4 = 2;
                    t3 = 2;
                }
                else
                {
                    c = t & 0x1F;
                    if (((t >> 5) & 3) == 3)
                    {
                        t4 = 2;
                        t3 = (t >> 7) & 1;
                    }
                    else
                    {
                        t4 = (t >> 7) & 1;
                        t3 = (t >> 5) & 3;
                    }
                }

                if ((c & 3) == 3)
                {
                    t2 = 2;
                    t1 = (c >> 4) & 1;
                    t0 = (((c >> 3) & 1) << 1) | (((c >> 2) & 1) & (~(c >> 3) & 1));
                }
                else if (((c >> 2) & 3) == 3)
                {
                    t2 = 2;
                    t1 = 2;
                    t0 = c & 3;
                }
                else
                {
                    t2 = (c >> 4) & 1;
                    t1 = (c >> 2) & 3;
                    t0 = (((c >> 1) & 1) << 1) | ((c & 1) & (~(c >> 1) & 1));
                }

                table[t * 5] = (byte)t0;
                table[t * 5 + 1] = (byte)t1;
                table[t * 5 + 2] = (byte)t2;
                table[t * 5 + 3] = (byte)t3;
                table[t * 5 + 4] = (byte)t4;
            }
            return table;
        }

        private static byte[] BuildQuintTable()
        {
            var table = new byte[128 * 3];
            for (int q = 0; q < 128; q++)
            {
                int q0, q1, q2;
                if (((q >> 1) & 3) == 3 && ((q >> 5) & 3) == 0)
                {
                    q0 = 4;
                    q1 = 4;
                    int nq0 = ~q & 1;
                    q2 = ((q & 1) << 2) | ((((q >> 4) & 1) & nq0) << 1) | (((q >> 3) & 1) & nq0);
                }
                else
                {
                    int c;
                    if (((q >> 1) & 3) == 3)
                    {
                        q2 = 4;
                        c = (((q >> 3) & 3) << 3) | ((~(q >> 5) & 3) << 1) | (q & 1);
                    }
                    else
                    {
                        q2 = (q >> 5) & 3;
                        c = q & 0x1F;
                    }

                    if ((c & 7) == 5)
                    {
                        q1 = 4;
                        q0 = (c >> 3) & 3;
                    }
                    else
                    {
                        q1 = (c >> 3) & 3;
                        q0 = c & 7;
                    }
                }
                table[q * 3] = (byte)q0;
                table[q * 3 + 1] = (byte)q1;
                table[q * 3 + 2] = (byte)q2;
            }
            return table;
        }

        private static int Replicate(int value, int bits, int target)
        {
            int result = 0;
            for (int shift = target - bits; shift > -bits; shift -= bits)
                result |= shift >= 0 ? value << shift : value >> -shift;
            return result;
        }

        private static byte[] BuildColorUnquant()
        {
            var table = new byte[RangeCount * 256];
            for (int r = 4; r < RangeCount; r++)
            {
                int m = ExtraBits[r];
                for (int v = 0; v < Levels[r]; v++)
                {
                    int result;
                    if (Kind[r] == 0)
                    {
                        result = Replicate(v, m, 8);
                    }
                    else
                    {
                        int d = v >> m;
                        int bits = v & ((1 << m) - 1);
                        int a = (bits & 1) != 0 ? 0x1FF : 0;
                        int b1 = (bits >> 1) & 1, b2 = (bits >> 2) & 1, b3 = (bits >> 3) & 1, b4 = (bits >> 4) & 1, b5 = (bits >> 5) & 1;
                        int b, c;
                        if (Kind[r] == 1)
                        {
                            c = m switch { 1 => 204, 2 => 93, 3 => 44, 4 => 22, 5 => 11, _ => 5 };
                            b = m switch
                            {
                                1 => 0,
                                2 => (b1 << 8) | (b1 << 4) | (b1 << 2) | (b1 << 1),
                                3 => (b2 << 8) | (b1 << 7) | (b2 << 3) | (b1 << 2) | (b2 << 1) | b1,
                                4 => (b3 << 8) | (b2 << 7) | (b1 << 6) | (b3 << 2) | (b2 << 1) | b1,
                                5 => (b4 << 8) | (b3 << 7) | (b2 << 6) | (b1 << 5) | (b4 << 1) | b3,
                                _ => (b5 << 8) | (b4 << 7) | (b3 << 6) | (b2 << 5) | (b1 << 4) | b5,
                            };
                        }
                        else
                        {
                            c = m switch { 1 => 113, 2 => 54, 3 => 26, 4 => 13, _ => 6 };
                            b = m switch
                            {
                                1 => 0,
                                2 => (b1 << 8) | (b1 << 3) | (b1 << 2),
                                3 => (b2 << 8) | (b1 << 7) | (b2 << 2) | (b1 << 1) | b2,
                                4 => (b3 << 8) | (b2 << 7) | (b1 << 6) | (b3 << 1) | b2,
                                _ => (b4 << 8) | (b3 << 7) | (b2 << 6) | (b1 << 5) | b4,
                            };
                        }

                        int t = (d * c + b) ^ a;
                        result = (a & 0x80) | (t >> 2);
                    }
                    table[r * 256 + v] = (byte)result;
                }
            }
            return table;
        }

        private static byte[] BuildWeightUnquant()
        {
            var table = new byte[12 * 32];
            for (int r = 0; r < 12; r++)
            {
                int m = ExtraBits[r];
                for (int v = 0; v < Levels[r]; v++)
                {
                    int result;
                    if (Kind[r] == 0)
                    {
                        result = Replicate(v, m, 6);
                    }
                    else if (m == 0)
                    {
                        result = Kind[r] == 1
                            ? v switch { 0 => 0, 1 => 32, _ => 63 }
                            : v switch { 0 => 0, 1 => 16, 2 => 32, 3 => 47, _ => 63 };
                    }
                    else
                    {
                        int d = v >> m;
                        int bits = v & ((1 << m) - 1);
                        int a = (bits & 1) != 0 ? 0x7F : 0;
                        int b1 = (bits >> 1) & 1, b2 = (bits >> 2) & 1;
                        int b, c;
                        if (Kind[r] == 1)
                        {
                            c = m switch { 1 => 50, 2 => 23, _ => 11 };
                            b = m switch
                            {
                                1 => 0,
                                2 => (b1 << 6) | (b1 << 2) | b1,
                                _ => (b2 << 6) | (b1 << 5) | (b2 << 1) | b1,
                            };
                        }
                        else
                        {
                            c = m == 1 ? 28 : 13;
                            b = m == 1 ? 0 : (b1 << 6) | (b1 << 1);
                        }

                        int t = (d * c + b) ^ a;
                        result = (a & 0x20) | (t >> 2);
                    }
                    table[r * 32 + v] = (byte)(result > 32 ? result + 1 : result);
                }
            }
            return table;
        }
    }
}
