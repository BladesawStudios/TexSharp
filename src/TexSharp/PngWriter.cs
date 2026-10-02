using System.IO.Compression;

namespace TexSharp;

/// <summary>Writes RGBA8 pixels as an 8-bit RGBA PNG.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int stride = checked(width * 4);
        if (rgba.Length < checked(stride * height))
            throw new ArgumentException($"A {width}x{height} image needs {stride * height} bytes, got {rgba.Length}.", nameof(rgba));

        using MemoryStream png = new();
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        WriteBigEndian(header, width);
        WriteBigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(png, "IHDR"u8, header);

        // Each scanline is a filter byte (none) and the row.
        byte[] raw = new byte[checked(height * (1 + stride))];
        for (int y = 0; y < height; y++)
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (1 + stride) + 1));

        using MemoryStream deflated = new();
        using (ZLibStream zlib = new(deflated, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(png, "IDAT"u8, deflated.ToArray());

        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    public static void Save(string path, ReadOnlySpan<byte> rgba, int width, int height)
        => File.WriteAllBytes(path, Encode(rgba, width, height));

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        WriteBigEndian(word, data.Length);
        stream.Write(word);
        stream.Write(type);
        stream.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        WriteBigEndian(word, (int)(crc ^ 0xFFFFFFFFu));
        stream.Write(word);
    }

    private static void WriteBigEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
