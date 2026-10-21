namespace TexSharp;

/// <summary>Maps signed (snorm or sint) channel values onto unsigned ones, the way TexSharp shows every signed format.</summary>
/// <remarks>
/// -1 becomes 0 and 1 becomes the top of the range. The most negative integer reads as -1 (as D3D has it), so -128 and
/// -127 are the same value in 8 bits.
/// </remarks>
internal static class SignedValues
{
    public static byte ToByte(int value) => (byte)((Math.Max(value, -127) + 127) * 255 / 254);

    public static float ToUnit(double value) => (float)((Math.Max(value, -1) + 1) / 2);
}
