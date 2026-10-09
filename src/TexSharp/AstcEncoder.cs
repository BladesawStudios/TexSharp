using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace TexSharp;

// An ASTC encoder for LDR 2D blocks, built from the decoder's own tables so what it writes is what the decoder reads.
// It writes one partition, one weight plane, and an RGB(A) direct endpoint pair (colour endpoint mode 8 or 12) with a
// weight grid (and quantisation of both) picked per block from a short list of block modes by decoding each candidate and
// keeping the closest. Blocks of one colour become void-extent blocks. It leaves out partitions, dual planes, luminance
// modes and the delta endpoint modes, which is where a full encoder gets most of its last few dB.
internal static partial class AstcDecoder
{
    private readonly record struct Candidate(int BlockMode, int GridWidth, int GridHeight, int WeightRange, int ColorRange, int WeightBits);

    private static readonly ConcurrentDictionary<(int Width, int Height, int Values), Candidate[]> Candidates = new();

    // The block mode fields the decoder reads, in the same order, for a block mode that has one weight plane.
    private static bool TryParseBlockMode(int blockMode, out int wx, out int wy, out int weightRange, out bool dual)
    {
        wx = wy = weightRange = 0;
        dual = false;
        if ((blockMode & 0x1FF) == 0x1FC) return false;

        int a = (blockMode >> 5) & 3;
        int high = (blockMode >> 9) & 1;
        int dualBit = (blockMode >> 10) & 1;
        int rq = (blockMode >> 4) & 1;
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

        dual = dualBit != 0;
        weightRange = high * 6 + rq - 2;
        return true;
    }

    // A handful of block modes worth trying for this footprint: the best few by a rough guess at their error, plus the
    // ones with the most weights and with the most colour precision.
    private static Candidate[] GetCandidates(int bw, int bh, int values)
        => Candidates.GetOrAdd((bw, bh, values), key => BuildCandidates(key.Width, key.Height, key.Values));

    private static Candidate[] BuildCandidates(int bw, int bh, int values)
    {
        List<(Candidate Mode, double Guess)> all = [];
        for (int blockMode = 0; blockMode < 2048; blockMode++)
        {
            if (!TryParseBlockMode(blockMode, out int wx, out int wy, out int weightRange, out bool dual) || dual) continue;
            int weightCount = wx * wy;
            if (wx > bw || wy > bh || weightCount > 64) continue;
            int weightBits = Tables.IseBits(weightCount, weightRange);
            if (weightBits < 24 || weightBits > 96) continue;

            int colorRange = -1;
            for (int r = Tables.RangeCount - 1; r >= 4; r--)
            {
                if (17 + Tables.IseBits(values, r) <= 128 - weightBits) { colorRange = r; break; }
            }
            if (colorRange < 0) continue;

            double colorStep = 255.0 / (Tables.LevelCount(colorRange) - 1);
            double weightStep = 128.0 / (Tables.LevelCount(weightRange) - 1);
            double coverage = Math.Min(1.0, weightCount / (double)(bw * bh));
            double guess = colorStep * colorStep + weightStep * weightStep + 128.0 * 128.0 * 0.15 * (1 - coverage) * (1 - coverage);
            all.Add((new Candidate(blockMode, wx, wy, weightRange, colorRange, weightBits), guess));
        }

        List<Candidate> picked = [];
        void Add(Candidate c)
        {
            if (!picked.Contains(c)) picked.Add(c);
        }

        foreach (var (mode, _) in all.OrderBy(x => x.Guess).Take(3)) Add(mode);
        var full = all.Where(x => x.Mode.GridWidth == bw && x.Mode.GridHeight == bh).OrderBy(x => x.Guess).Take(1);
        foreach (var (mode, _) in full) Add(mode);
        foreach (var (mode, _) in all.OrderByDescending(x => x.Mode.ColorRange).ThenBy(x => x.Guess).Take(1)) Add(mode);
        return [.. picked];
    }

    /// <summary>
    /// Encodes <paramref name="rgba"/> (width * height * 4 bytes) into <paramref name="blocks"/> (16 bytes per block, row-major).
    /// </summary>
    public static void Encode(ReadOnlySpan<byte> rgba, int width, int height, int blockWidth, int blockHeight, Span<byte> blocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockWidth, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockWidth, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockHeight, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockHeight, 12);

        int blocksX = (width + blockWidth - 1) / blockWidth;
        int blocksY = (height + blockHeight - 1) / blockHeight;
        Footprint footprint = Footprint.Get(blockWidth, blockHeight);
        Candidate[] rgbModes = GetCandidates(blockWidth, blockHeight, 6);
        Candidate[] rgbaModes = GetCandidates(blockWidth, blockHeight, 8);
        byte[] source = rgba[..(width * height * 4)].ToArray();
        byte[] output = new byte[blocksX * blocksY * 16];

        Parallel.For(0, blocksY, by =>
        {
            var state = new BlockState(footprint);
            Span<byte> texels = stackalloc byte[blockWidth * blockHeight * 4];
            for (int bx = 0; bx < blocksX; bx++)
            {
                TextureEncoder.Gather(source, width, height, bx * blockWidth, by * blockHeight, blockWidth, blockHeight, texels);
                state.EncodeBlock(texels, rgbModes, rgbaModes, output.AsSpan((by * blocksX + bx) * 16, 16));
            }
        });
        output.CopyTo(blocks);
    }

    private sealed class BlockState
    {
        private readonly Footprint _footprint;
        private readonly int _texels;
        private readonly byte[] _scratch = new byte[ScratchBytes];
        private readonly int[] _ints = new int[ScratchInts];
        private readonly float[] _points;
        private readonly float[] _target;
        private readonly float[] _grid = new float[64];
        private readonly float[] _blend;
        private readonly byte[] _weightIndex = new byte[64];

        public BlockState(Footprint footprint)
        {
            _footprint = footprint;
            _texels = footprint.Width * footprint.Height;
            _points = new float[_texels * 4];
            _target = new float[_texels];
            _blend = new float[_texels];
        }

        public void EncodeBlock(ReadOnlySpan<byte> texels, Candidate[] rgbModes, Candidate[] rgbaModes, Span<byte> output)
        {
            bool uniform = true, opaque = true;
            for (int i = 0; i < _texels; i++)
            {
                _points[i * 4] = texels[i * 4];
                _points[i * 4 + 1] = texels[i * 4 + 1];
                _points[i * 4 + 2] = texels[i * 4 + 2];
                _points[i * 4 + 3] = texels[i * 4 + 3];
                if (!texels.Slice(i * 4, 4).SequenceEqual(texels[..4])) uniform = false;
                if (texels[i * 4 + 3] != 255) opaque = false;
            }

            if (uniform)
            {
                WriteVoidExtent(texels[..4], output);
                return;
            }

            int cem = opaque ? 8 : 12;
            Candidate[] modes = opaque ? rgbModes : rgbaModes;
            int values = opaque ? 6 : 8;

            Span<float> mean = stackalloc float[4], axis = stackalloc float[4], e0 = stackalloc float[4], e1 = stackalloc float[4];
            Fit.PrincipalAxis(_points, _texels, 4, mean, axis);
            Fit.Extremes(_points, _texels, 4, mean, axis, e0, e1);

            long bestError = long.MaxValue;
            Span<byte> trial = stackalloc byte[16];
            Span<float> start0 = stackalloc float[4], start1 = stackalloc float[4];
            foreach (Candidate candidate in modes)
            {
                e0.CopyTo(start0);
                e1.CopyTo(start1);
                if (!TryEncode(candidate, cem, values, start0, start1, texels, trial, out long error)) continue;
                if (error >= bestError) continue;
                bestError = error;
                trial.CopyTo(output);
                if (error == 0) break;
            }

            if (bestError == long.MaxValue) WriteVoidExtent(texels[..4], output);
        }

        private static void WriteVoidExtent(ReadOnlySpan<byte> rgba, Span<byte> output)
        {
            ulong lo = 0x1FC | (3UL << 10) | (~0UL << 12);
            ulong hi = 0;
            for (int c = 0; c < 4; c++) hi |= (ulong)(rgba[c] * 257) << (16 * c);
            BinaryPrimitives.WriteUInt64LittleEndian(output, lo);
            BinaryPrimitives.WriteUInt64LittleEndian(output[8..], hi);
        }

        private bool TryEncode(Candidate c, int cem, int values, Span<float> e0, Span<float> e1, ReadOnlySpan<byte> texels, Span<byte> output, out long error)
        {
            error = long.MaxValue;
            int weightCount = c.GridWidth * c.GridHeight;
            Footprint.Infill infill = _footprint.GetInfill(c.GridWidth, c.GridHeight);
            Span<byte> endpointIndex = stackalloc byte[8];
            Span<int> decoded0 = stackalloc int[4], decoded1 = stackalloc int[4];
            bool haveBest = false;
            ulong bestLo = 0, bestHi = 0;

            // Fit the weights to the endpoints, then the endpoints to the weights, twice.
            for (int pass = 0; pass < 2; pass++)
            {
                QuantizeEndpoints(c.ColorRange, values, e0, e1, endpointIndex, decoded0, decoded1);
                FitWeights(c, infill, decoded0, decoded1);

                ulong lo = 0, hi = 0;
                PackBlock(c, cem, values, endpointIndex, ref lo, ref hi);

                BinaryPrimitives.WriteUInt64LittleEndian(output, lo);
                BinaryPrimitives.WriteUInt64LittleEndian(output[8..], hi);
                long e = Measure(lo, hi, texels);
                if (e >= 0 && (!haveBest || e < error))
                {
                    haveBest = true;
                    error = e;
                    bestLo = lo;
                    bestHi = hi;
                }
                if (e == 0 || pass == 1) break;

                // The weights the decoder will actually see, to solve the endpoints against.
                ReadOnlySpan<byte> unquant = Tables.WeightUnquant.AsSpan(c.WeightRange * 32, 32);
                for (int i = 0; i < _texels; i++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += unquant[_weightIndex[infill.Index[i * 4 + k]]] * infill.Weight[i * 4 + k];
                    _blend[i] = ((sum + 8) >> 4) / 64f;
                }
                if (!Fit.Solve(_blend, _points, _texels, 4, e0, e1)) break;
            }

            if (!haveBest) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(output, bestLo);
            BinaryPrimitives.WriteUInt64LittleEndian(output[8..], bestHi);
            return true;
        }

        // Squared error of the block against the source, or -1 if the decoder won't read the block.
        private long Measure(ulong lo, ulong hi, ReadOnlySpan<byte> texels)
        {
            Span<byte> px = _scratch.AsSpan(0, _texels * 4);
            Span<byte> work = _scratch.AsSpan(MaxTexels * 4);
            if (!DecodeBlock(lo, hi, _footprint, px, work, _ints)) return -1;
            long error = 0;
            for (int i = 0; i < px.Length; i++)
            {
                int d = px[i] - texels[i];
                error += d * d;
            }
            return error;
        }

        // Quantises the endpoints (RGB(A) as float) to the colour range, ordered the way the direct modes want
        // (the second endpoint's RGB sum at least the first's), and reports what the decoder will read back.
        private static void QuantizeEndpoints(int range, int values, Span<float> e0, Span<float> e1, Span<byte> index, Span<int> decoded0, Span<int> decoded1)
        {
            ReadOnlySpan<byte> unquant = Tables.ColorUnquant.AsSpan(range * 256, 256);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                for (int ch = 0; ch < values / 2; ch++)
                {
                    index[ch * 2] = Tables.QuantizeColor(range, (int)MathF.Round(e0[ch]));
                    index[ch * 2 + 1] = Tables.QuantizeColor(range, (int)MathF.Round(e1[ch]));
                }
                int sum0 = unquant[index[0]] + unquant[index[2]] + unquant[index[4]];
                int sum1 = unquant[index[1]] + unquant[index[3]] + unquant[index[5]];
                if (sum1 >= sum0) break;
                for (int ch = 0; ch < 4; ch++) (e0[ch], e1[ch]) = (e1[ch], e0[ch]);
            }

            for (int ch = 0; ch < 4; ch++)
            {
                decoded0[ch] = ch < values / 2 ? unquant[index[ch * 2]] : 255;
                decoded1[ch] = ch < values / 2 ? unquant[index[ch * 2 + 1]] : 255;
            }
        }

        // Picks the quantised grid weights that best give each texel's position along the endpoint line.
        private void FitWeights(Candidate c, Footprint.Infill infill, ReadOnlySpan<int> d0, ReadOnlySpan<int> d1)
        {
            int weightCount = c.GridWidth * c.GridHeight;
            float length = 0;
            Span<float> direction = stackalloc float[4];
            for (int ch = 0; ch < 4; ch++)
            {
                direction[ch] = d1[ch] - d0[ch];
                length += direction[ch] * direction[ch];
            }

            for (int i = 0; i < _texels; i++)
            {
                float u = 0;
                if (length > 0)
                    for (int ch = 0; ch < 4; ch++) u += (_points[i * 4 + ch] - d0[ch]) * direction[ch];
                _target[i] = length > 0 ? Math.Clamp(u / length, 0f, 1f) : 0f;
            }

            // Start each grid weight from the texel at its place in the block, then nudge them so the filled-in
            // weights follow the targets (a diagonal least-squares step, repeated).
            int bw = _footprint.Width, bh = _footprint.Height;
            for (int gy = 0; gy < c.GridHeight; gy++)
                for (int gx = 0; gx < c.GridWidth; gx++)
                {
                    int x = (int)MathF.Round(gx * (bw - 1) / (float)(c.GridWidth - 1));
                    int y = (int)MathF.Round(gy * (bh - 1) / (float)(c.GridHeight - 1));
                    _grid[gy * c.GridWidth + gx] = _target[y * bw + x];
                }

            Span<float> numerator = stackalloc float[64], denominator = stackalloc float[64];
            bool exact = c.GridWidth == bw && c.GridHeight == bh;
            for (int iteration = 0; iteration < (exact ? 0 : 6); iteration++)
            {
                numerator[..weightCount].Clear();
                denominator[..weightCount].Clear();
                for (int i = 0; i < _texels; i++)
                {
                    float predicted = 0;
                    for (int k = 0; k < 4; k++) predicted += _grid[infill.Index[i * 4 + k]] * infill.Weight[i * 4 + k] / 16f;
                    float residual = _target[i] - predicted;
                    for (int k = 0; k < 4; k++)
                    {
                        float f = infill.Weight[i * 4 + k] / 16f;
                        numerator[infill.Index[i * 4 + k]] += f * residual;
                        denominator[infill.Index[i * 4 + k]] += f * f;
                    }
                }
                for (int j = 0; j < weightCount; j++)
                    if (denominator[j] > 1e-6f) _grid[j] = Math.Clamp(_grid[j] + 0.8f * numerator[j] / denominator[j], 0f, 1f);
            }

            for (int j = 0; j < weightCount; j++)
                _weightIndex[j] = Tables.QuantizeWeight(c.WeightRange, (int)MathF.Round(_grid[j] * 64f));
        }

        private void PackBlock(Candidate c, int cem, int values, ReadOnlySpan<byte> endpointIndex, ref ulong lo, ref ulong hi)
        {
            Tables.SetBits(ref lo, ref hi, 0, (uint)c.BlockMode, 11);
            Tables.SetBits(ref lo, ref hi, 13, (uint)cem, 4);

            // The endpoint values go in the order the direct modes read them: R0 R1 G0 G1 B0 B1 (A0 A1).
            Tables.WriteIse(ref lo, ref hi, 17, endpointIndex[..values], c.ColorRange);

            ulong weightLo = 0, weightHi = 0;
            Tables.WriteIse(ref weightLo, ref weightHi, 0, _weightIndex.AsSpan(0, c.GridWidth * c.GridHeight), c.WeightRange);
            lo |= ReverseBits(weightHi);
            hi |= ReverseBits(weightLo);
        }
    }

    internal static partial class Tables
    {
        // Built on first use, after the decoder's tables exist (static initialisers of a partial class run in no set order).
        private static class Quant
        {
            public static readonly byte[] Color = BuildColor();
            public static readonly byte[] Weight = BuildWeight();
            public static readonly short[][] Trits = BuildInverse(5, 3, TritTable, [2, 4, 5, 7, 8]);
            public static readonly short[][] Quints = BuildInverse(3, 5, QuintTable, [3, 5, 7]);
        }

        public static int LevelCount(int range) => Levels[range];

        public static byte QuantizeColor(int range, int value) => Quant.Color[range * 256 + Math.Clamp(value, 0, 255)];

        public static byte QuantizeWeight(int range, int value) => Quant.Weight[range * 65 + Math.Clamp(value, 0, 64)];

        // For each target value 0..255, the quantised index whose reconstructed value is nearest.
        private static byte[] BuildColor()
        {
            byte[] table = new byte[RangeCount * 256];
            for (int r = 4; r < RangeCount; r++)
                for (int v = 0; v < 256; v++)
                    table[r * 256 + v] = Nearest(ColorUnquant, r * 256, Levels[r], v);
            return table;
        }

        private static byte[] BuildWeight()
        {
            byte[] table = new byte[12 * 65];
            for (int r = 0; r < 12; r++)
                for (int v = 0; v <= 64; v++)
                    table[r * 65 + v] = Nearest(WeightUnquant, r * 32, Levels[r], v);
            return table;
        }

        private static byte Nearest(byte[] unquant, int offset, int levels, int value)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < levels; i++)
            {
                int d = Math.Abs(unquant[offset + i] - value);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return (byte)best;
        }

        // For a group of n (1..groupSize) trits or quints, the packed code that decodes to those digits and whose bits
        // above the ones written for n values are zero (the decoder only reads the written bits). Indexed [n][digits in
        // base radix, first digit lowest]. writtenBits[n - 1] is how many bits of the code n values carry.
        private static short[][] BuildInverse(int groupSize, int radix, byte[] decode, int[] writtenBits)
        {
            short[][] result = new short[groupSize + 1][];
            int codes = groupSize == 5 ? 256 : 128;
            for (int n = 1; n <= groupSize; n++)
            {
                int combos = 1;
                for (int i = 0; i < n; i++) combos *= radix;
                short[] table = new short[combos];
                Array.Fill(table, (short)-1);
                for (int code = 0; code < codes; code++)
                {
                    if ((code >> writtenBits[n - 1]) != 0) continue;
                    int key = 0, scale = 1;
                    for (int i = 0; i < n; i++)
                    {
                        key += decode[code * groupSize + i] * scale;
                        scale *= radix;
                    }
                    if (table[key] < 0) table[key] = (short)code;
                }
                result[n] = table;
            }
            return result;
        }

        public static void SetBits(ref ulong lo, ref ulong hi, int position, uint value, int count)
        {
            for (int k = 0; k < count; k++, position++)
            {
                if (((value >> k) & 1) == 0) continue;
                if (position < 64) lo |= 1UL << position;
                else hi |= 1UL << (position - 64);
            }
        }

        // The inverse of DecodeIse.
        public static void WriteIse(ref ulong lo, ref ulong hi, int position, ReadOnlySpan<byte> indices, int range)
        {
            int m = ExtraBits[range];
            uint mask = (1u << m) - 1;
            switch (Kind[range])
            {
                case 0:
                    foreach (byte index in indices)
                    {
                        SetBits(ref lo, ref hi, position, index, m);
                        position += m;
                    }
                    break;
                case 1:
                    for (int g = 0; g < indices.Length; g += 5)
                    {
                        int n = Math.Min(5, indices.Length - g);
                        int key = 0, scale = 1;
                        for (int i = 0; i < n; i++)
                        {
                            key += (indices[g + i] >> m) * scale;
                            scale *= 3;
                        }
                        uint t = (uint)Quant.Trits[n][key];
                        for (int i = 0; i < n; i++)
                        {
                            SetBits(ref lo, ref hi, position, indices[g + i] & mask, m);
                            position += m;
                            int chunk = i is 2 or 4 ? 1 : 2;
                            int shift = i switch { 0 => 0, 1 => 2, 2 => 4, 3 => 5, _ => 7 };
                            SetBits(ref lo, ref hi, position, (t >> shift) & ((1u << chunk) - 1), chunk);
                            position += chunk;
                        }
                    }
                    break;
                default:
                    for (int g = 0; g < indices.Length; g += 3)
                    {
                        int n = Math.Min(3, indices.Length - g);
                        int key = 0, scale = 1;
                        for (int i = 0; i < n; i++)
                        {
                            key += (indices[g + i] >> m) * scale;
                            scale *= 5;
                        }
                        uint q = (uint)Quant.Quints[n][key];
                        for (int i = 0; i < n; i++)
                        {
                            SetBits(ref lo, ref hi, position, indices[g + i] & mask, m);
                            position += m;
                            int chunk = i == 0 ? 3 : 2;
                            int shift = i switch { 0 => 0, 1 => 3, _ => 5 };
                            SetBits(ref lo, ref hi, position, (q >> shift) & ((1u << chunk) - 1), chunk);
                            position += chunk;
                        }
                    }
                    break;
            }
        }
    }
}
