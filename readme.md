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
other uncompressed formats use pixel masks. Only 2D, single-slice surfaces are handled.

R8, RG8, R5G6B5 and RGBA4 are awkward in an image editor, so `PixelFormats.ExpandToRgba8` and
`CollapseRgba8` turn them into an RGBA8 image and back. For an unedited image that round trip is exact.

## Decoding

| Format | |
| --- | --- |
| BC1, BC2, BC3 | Exact against Pillow's decoder. A BC1 block with its first colour not above its second has one transparent black entry, as in D3D; BC2 and BC3 colour blocks are always four-colour. |
| BC4, BC5 | Exact against Pillow. BC4 comes out as grey, BC5 as red and green with blue zero; `ReconstructNormalZ` fills in blue for a normal map. |
| BC6 | HDR is clamped to 0..1 and rounded. All 14 modes agree with Pillow, which truncates instead of rounding, so values may be one higher. |
| BC7 | Exact against Pillow. A reserved block gives zeros. |
| ASTC, all 14 footprints | LDR only. |
| R8, RG8, R5G6B5, RGBA4, RGB10A2, RGBA8, BGRA8 | Straight conversion. |

Not covered: signed BC4, BC5 and BC6 (SNORM) follow the D3D rules but were not checked against another
decoder; ASTC HDR; ETC, EAC and PVRTC; encoding to any compressed format.

R5G5B5A1, A1B5G5R5, B5G6R5, A4B4G4R4 and B5G5R5A1 have no DDS equivalent, so `BntxFormats` carries
them as R5G6B5 and RGBA4 of the same size. The bytes round trip untouched but the channels are not
read as the format the BNTX declares.

## Checking it

`tests/TexSharp.Verify` compares the decoders with reference output and DDS files from another
writer; see the comment at the top of its `Program.cs` for the commands.

## Build

```bash
dotnet build TexSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
