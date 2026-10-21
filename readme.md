# TexSharp

DDS reading and writing, and decoding to RGBA8, for the textures in Nintendo Switch **BNTX** files
(*Tears of the Kingdom*, *Breath of the Wild*, and anything else that uses the same formats).

It works on plain byte arrays and depends on nothing. [BntxSharp](../BntxSharp) and
[TxtgSharp](../TxtgSharp) use it for `ToRgba8`, `ToDds` and `ReplaceFromDds` on their textures.

```csharp
BntxTexture tex = bntx.Textures[0];
byte[] level = tex.GetDeswizzledData(0);                       // BntxSharp

BntxFormats.TryFromBntx((uint)tex.Format, out var format, out bool srgb, out bool snorm);

// Look at it.
byte[] rgba = TextureDecoder.ToRgba8(format, level, tex.Width, tex.Height, snorm);

// Hand it to an image editor, without touching the pixels.
var dds = new DdsImage(tex.Width, tex.Height, format, [level], srgb, snorm);
File.WriteAllBytes("texture.dds", dds.ToBytes());

// Bring the edited file back in.
DdsImage edited = DdsImage.Parse(File.ReadAllBytes("texture.dds"));
uint formatId = BntxFormats.ToBntx(edited.Format, edited.IsSrgb, edited.IsSnorm);
```

## DDS

`DdsImage` carries raw block or pixel data, split per mip, and never re-encodes it, so a level goes to
DDS and back unchanged. Files are laid out the way Switch Toolbox writes them, so each tool reads the
other's output: BC1 to BC5 use the legacy FourCCs, RGBA8, BGRA8, BC6, BC7 and ASTC use the DX10 header
(ASTC with the unofficial values `ASTC_4x4_UNORM = 134` to `ASTC_12x12_UNORM_SRGB = 187`), and the
other 8-bit-or-less uncompressed formats use pixel masks. Formats with wider channels (R16, RG16, RGBA16, R32 to
RGBA32), the float ones, RGB9E5, depth, and signed R8, RG8 and RGBA8 use the DX10 header with their DXGI value,
since masks can't describe them. B10G11R11 float has no DXGI equivalent and can't be written. Only 2D,
single-slice surfaces are handled.

R8, RG8 and the 16-bit packed formats (R5G6B5, RGBA4 and the rest) are awkward in an image editor, so
`PixelFormats.ExpandToRgba8` and `CollapseRgba8` turn them into an RGBA8 image and back. For an unedited image that
round trip is exact.

## Converting

`TextureConverter.Convert(dds, format, width, height, mipCount, srgb)` makes the mip data for a texture of that size and
format from any DDS: it decodes, resizes if the size differs (alpha-weighted, in linear light for sRGB), keeps the DDS's own
mips when the size already matches and there are enough, otherwise rebuilds the chain, and encodes each level with
`TextureEncoder.Encode`. `BntxSharp` and `TxtgSharp` use it in `ReplaceLayerFromDds` so a layer of an array texture takes
whatever DDS it is given. `TextureConverter.Differences` lists what would change.

The encoders are written for this library and aim to land close to the source, not to beat dedicated compressors: BC1
(with one-bit alpha), BC2, BC3, BC4, BC5 and BC7 (mode 6 only) pick endpoints along the main axis of each block and refine
them by least squares; ASTC (all 2D footprints, LDR) writes one partition per block and tries a few block modes per block,
keeping whichever decodes closest. Checked by `TexSharp.Verify encode`, which round-trips a picture through every format
and prints the PSNR. The wide and float formats take the 8-bit values scaled (`v * 257` for 16 bits, `v / 255` as a float).
BC6H, signed formats, RGB9E5 and depth are not encodable.

## JPEG

`JpegWriter.Encode(rgba, width, height, quality: 75)` writes a baseline JPEG (4:2:0, the standard tables, alpha dropped) so a
texture can be exported for programs that want one. It matches Pillow's default output to about 47 dB PSNR, within 1% in size.

## Looking at a texture

`TextureDecoder.ToRgba8` gives the channels as stored. `TextureDecoder.Render` applies the texture's channel
swizzle as well (a `ChannelMap`, from `ChannelMap.FromBntx` or `ChannelMap.FromTxtg`) and, for a BC5 tangent-space
normal map, rebuilds the blue channel. `PngWriter.Encode` turns the result into a PNG. Both `BntxTexture` and
`TxtgFile` have `Render` and `ToPng` that do this with their own channel data.

## Decoding

| Format | |
| --- | --- |
| BC1, BC2, BC3 | Exact against Pillow's decoder. A BC1 block with its first colour not above its second has one transparent black entry, as in D3D; BC2 and BC3 colour blocks are always four-colour. |
| BC4, BC5 | Exact against Pillow. BC4 comes out as grey, BC5 as red and green with blue zero; `ReconstructNormalZ` fills in blue for a normal map. |
| BC6 | HDR is clamped to 0..1 and rounded. All 14 modes agree with Pillow, which truncates instead of rounding, so values may be one higher. |
| BC7 | Exact against Pillow. A reserved block gives zeros. |
| ASTC, all 14 footprints | LDR only. |
| R8, RG8, R5G6B5, RGBA4, RGB10A2, RGBA8, BGRA8 | Straight conversion. Signed R8, RG8 and RGBA8 are remapped from -1..1 to 0..255. |
| R4G4, B5G6R5, A4B4G4R4, R5G5B5A1, A1B5G5R5, B5G5R5A1 | Straight conversion, bits replicated. |
| R16, RG16, RGBA16, R32, RG32, RGB32, RGBA32 | Read as normalized whether the BNTX says UNORM or UINT (SNORM or SINT when signed), so an R16 heightmap is black at 0 and white at 65535. |
| R16, RG16, RGBA16, R32, RG32, RGB32 and RGBA32 float, R11G11B10, B10G11R11, RGB9E5 | Clamped to 0..1, NaN as 0. |
| D24S8, D32 float S8 | Depth in red, stencil in green. |

One-channel formats come out as grey. Every one of these is checked against hand-worked pixels by `TexSharp.Verify self`;
none was compared with another decoder.

Not covered: signed BC4, BC5 and BC6 (SNORM) follow the D3D rules but were not checked against another
decoder; ASTC HDR; ETC, EAC and PVRTC; encoding to any compressed format.

BNTX names its 16-bit packed formats without saying which end of the word comes first. R5G6B5 and RGBA4 are read the
way Switch Toolbox has them (red in the high bits of R5G6B5; A, R, G, B from the top of RGBA4), B5G6R5 as R5G6B5 with red
and blue swapped, and R4G4, A4B4G4R4 and the three 5-5-5-1 layouts with their first-named channel in the lowest bits, as
nn::gfx names R10G10B10A2 and R11G11B10. None was checked against a game texture.

A BNTX format id is a layout in the high byte and a type in the low one (nn::gfx's TypeFormat: 1 UNORM, 2 SNORM,
3 UINT, 4 SINT, 5 FLOAT, 6 sRGB, 0x0A UFLOAT). `BntxFormats.TryFromBntx` reads FLOAT as the float format of the layout,
so `0x0A05` is R16 float and `0x1F05` is signed BC6 (`0x1F0A` unsigned).

The layouts follow nn::gfx's ChannelFormat order (R16_G16 is `0x12`, R16_G16_B16_A16 `0x15`, R32_G32_B32_A32 `0x19`),
which Switch Toolbox numbers one lower. Every one of TotK's 195 vertex animation textures is `0x1505` and holds four half
floats a texel. Nothing in TotK uses the other layouts in that range, so only `0x15` is confirmed; `0x11` is not read.

## Checking it

`tests/TexSharp.Verify` compares the decoders with reference output and DDS files from another
writer; see the comment at the top of its `Program.cs` for the commands.

## Build

```bash
dotnet build TexSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
