using System.Buffers.Binary;

namespace Sff;

/// <summary>
/// Reads SFFv1, the format MUGEN used before 1.0. Where v2 has node tables, v1
/// is a linked list of 32-byte subheaders each followed by a PCX image, and
/// palettes are carried by the PCX rather than stored separately.
/// </summary>
internal static class SffV1Reader
{
    private const int HeaderSize = 512;
    private const int SubHeaderSize = 32;
    /// <summary>Header byte 32: SprMaker 0.97's "Shared palette" type.</summary>
    private const byte SharedPalette = 1;


    public static SffFile Decode(byte[] b, SffVersion version)
    {
        if (b.Length < HeaderSize)
            throw new InvalidDataException($"file is {b.Length} bytes, shorter than the {HeaderSize} byte header");

        uint imageCount = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20));
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24));
        bool shared = b[32] == SharedPalette;

        var f = new SffFile { Version = version, CompatVersion = version, V1SharedPalette = shared };
        var linkTo = new List<int>();

        // Walk the list. Each subheader points at the next; a pointer that does
        // not advance ends it, which also guards against a malformed loop.
        uint off = first;
        while (off != 0 && (long)off + SubHeaderSize <= b.Length && f.Sprites.Count < (long)imageCount + 8)
        {
            var n = b.AsSpan((int)off, SubHeaderSize);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(n);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(n[4..]); // long: 2 GB+ must not turn negative

            var s = new Sprite
            {
                AxisX = BinaryPrimitives.ReadInt16LittleEndian(n[8..]),
                AxisY = BinaryPrimitives.ReadInt16LittleEndian(n[10..]),
                Group = BinaryPrimitives.ReadUInt16LittleEndian(n[12..]),
                Index = BinaryPrimitives.ReadUInt16LittleEndian(n[14..]),
                SrcFormat = SpriteFormat.Pcx,
                SrcDepth = 8,
            };
            int previousCopy = BinaryPrimitives.ReadUInt16LittleEndian(n[16..]);
            bool samePalette = n[18] != 0;

            f.Sprites.Add(s);
            linkTo.Add(length == 0 ? previousCopy : -1);

            if (length > 0)
            {
                int start = (int)off + SubHeaderSize;
                if (start + length > b.Length) length = b.Length - start;
                try
                {
                    var img = Pcx.Decode(b.AsSpan(start, (int)length), samePalette);
                    s.Width = img.Width;
                    s.Height = img.Height;
                    s.Indexed = img.Indices;
                    s.Palette = ChoosePalette(f, img.Palette, samePalette, shared);
                }
                catch (Exception e)
                {
                    s.DecodeError = e.Message;
                }
            }

            if (next <= off) break;
            off = next;
        }

        ResolveLinks(f, linkTo);
        return f;
    }

    /// <summary>A sprite carries its own palette, or its subheader flag says it has none: then it uses
    /// the first palette in a shared-palette file, else the previous one (see Official tools in features.md).</summary>
    private static Palette? ChoosePalette(SffFile f, Rgba[]? own, bool samePalette, bool shared)
    {
        if (samePalette || own is null)
            return f.Palettes.Count == 0 ? null : shared ? f.Palettes[0] : f.Palettes[^1];

        var p = new Palette { Group = 1, Index = f.Palettes.Count + 1, Colors = own };
        f.Palettes.Add(p);
        return p;
    }

    /// <summary>
    /// A zero-length subfile is a copy of an earlier sprite, named by its
    /// position in the list.
    /// </summary>
    private static void ResolveLinks(SffFile f, List<int> linkTo)
    {
        for (int i = 0; i < f.Sprites.Count; i++)
        {
            if (linkTo[i] < 0) continue;
            var s = f.Sprites[i];
            if (linkTo[i] >= f.Sprites.Count)
            {
                s.DecodeError = $"links to out-of-range sprite {linkTo[i]}";
                continue;
            }
            var target = f.Sprites[linkTo[i]];
            if (ReferenceEquals(target, s)) { s.DecodeError = "sprite links to itself"; continue; }

            s.SharesWith = target; // pixels and size are read through the link
            s.Palette ??= target.Palette;
        }
        Sprite.BreakLinkLoops(f.Sprites);
    }
}
