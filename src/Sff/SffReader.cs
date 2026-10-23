using System.Buffers.Binary;

namespace Sff;

/// <summary>Reads SFFv2 files. Layout constants come from the SFFv2 specification.</summary>
public static class SffReader
{
    internal const int HeaderSize = 512;
    internal const int SpriteNodeSize = 28;
    internal const int PaletteNodeSize = 16;

    internal static ReadOnlySpan<byte> Signature => "ElecbyteSpr\0"u8;

    private readonly record struct Header(
        SffVersion Version, SffVersion Compat,
        uint SprOffset, uint SprCount, uint PalOffset, uint PalCount,
        uint LdataOffset, uint LdataLen, uint TdataOffset, uint TdataLen);

    /// <summary>
    /// Parses an in-memory SFFv2 file. Structural problems throw; a sprite whose
    /// pixel data fails to decode is kept with DecodeError set, so one bad sprite
    /// does not stop the rest of the file from loading.
    /// </summary>
    public static SffFile Decode(byte[] b)
    {
        if (b.Length < 16)
            throw new InvalidDataException($"file is {b.Length} bytes, far too short to be an SFF");
        if (!b.AsSpan(0, 12).SequenceEqual(Signature))
            throw new InvalidDataException("bad signature, not an SFF file");

        // Both versions put the quad at the same offset, so it decides the layout.
        var version = new SffVersion(b[15], b[14], b[13], b[12]);
        return version.Hi switch
        {
            1 => SffV1Reader.Decode(b, version),
            2 => DecodeV2(b, version),
            _ => throw new InvalidDataException($"SFF version {version} is not supported"),
        };
    }

    private static SffFile DecodeV2(byte[] b, SffVersion version)
    {
        var h = ParseHeader(b, version);
        // Checked before allocating per-node arrays, so a corrupt count can't ask for gigabytes.
        if ((long)h.PalOffset + (long)h.PalCount * PaletteNodeSize > b.Length && h.PalCount > 0)
            throw new InvalidDataException($"the palette table ({h.PalCount} nodes) runs past the end of the file");
        if ((long)h.SprOffset + (long)h.SprCount * SpriteNodeSize > b.Length && h.SprCount > 0)
            throw new InvalidDataException($"the sprite table ({h.SprCount} nodes) runs past the end of the file");
        var f = new SffFile { Version = h.Version, CompatVersion = h.Compat };
        ReadPalettes(f, b, h);
        var links = ReadSprites(f, b, h);
        ResolveLinks(f, links);
        return f;
    }

    private static Header ParseHeader(byte[] b, SffVersion version)
    {
        if (b.Length < HeaderSize)
            throw new InvalidDataException($"file is {b.Length} bytes, shorter than the {HeaderSize} byte header");

        uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off));
        return new Header(
            version, new SffVersion(b[27], b[26], b[25], b[24]),
            U32(36), U32(40), U32(44), U32(48),
            U32(52), U32(56), U32(60), U32(64));
    }

    /// <summary>Reads the palette node table. Palette data always lives in ldata.</summary>
    private static void ReadPalettes(SffFile f, byte[] b, Header h)
    {
        var links = new int[h.PalCount];

        for (int i = 0; i < h.PalCount; i++)
        {
            int off = (int)h.PalOffset + i * PaletteNodeSize;
            if (off + PaletteNodeSize > b.Length)
                throw new InvalidDataException($"palette node {i} lies past the end of the file");
            var n = b.AsSpan(off, PaletteNodeSize);

            var p = new Palette
            {
                Group = BinaryPrimitives.ReadUInt16LittleEndian(n),
                Index = BinaryPrimitives.ReadUInt16LittleEndian(n[2..]),
            };
            int numCols = BinaryPrimitives.ReadUInt16LittleEndian(n[4..]);
            links[i] = BinaryPrimitives.ReadUInt16LittleEndian(n[6..]);
            long dataOff = h.LdataOffset + (long)BinaryPrimitives.ReadUInt32LittleEndian(n[8..]);
            long dataLen = BinaryPrimitives.ReadUInt32LittleEndian(n[12..]);
            f.Palettes.Add(p);

            if (dataLen == 0) continue; // linked palette, resolved below
            if (dataOff + dataLen > b.Length)
                throw new InvalidDataException($"palette {i} data lies past the end of the file");
            if (dataLen < 4 && numCols > 0) // used to read past the block; with no colors declared it loads empty, as before
                throw new InvalidDataException($"palette {i} data is {dataLen} bytes, shorter than one color");
            p.Colors = ParsePaletteData(b.AsSpan((int)dataOff, (int)dataLen), numCols);
            links[i] = -1;
        }

        // A link may point at another linked palette; follow to a palette with data.
        // A loop is left unlinked with no colors, as before.
        for (int i = 0; i < links.Length; i++)
        {
            if (links[i] < 0) continue;
            if (links[i] >= f.Palettes.Count)
                throw new InvalidDataException($"palette {i} links to out-of-range palette {links[i]}");
            int t = links[i];
            for (int steps = 0; t >= 0 && links[t] >= 0 && steps <= links.Length; steps++)
                t = links[t] < f.Palettes.Count ? links[t] : -1;
            if (t >= 0 && links[t] < 0 && t != i) f.Palettes[i].LinksTo = f.Palettes[t];
        }
    }

    /// <summary>
    /// Reads 4-byte entries. The fourth byte is documented as unused but real
    /// files store alpha there; it is only ignored when the whole table is zero,
    /// which would otherwise make every color invisible.
    ///
    /// The count in the palette node is not trusted: files exist where it
    /// disagrees with the size of the data block, and the block is the reliable
    /// one. The declared count is only used if there is no block to measure.
    /// </summary>
    private static Rgba[] ParsePaletteData(ReadOnlySpan<byte> d, int numCols)
    {
        int avail = d.Length / 4;
        numCols = avail > 0 ? avail : Math.Max(numCols, 0);

        var cols = new Rgba[numCols];
        bool anyAlpha = false;
        for (int i = 0; i < numCols; i++)
        {
            if (d[i * 4 + 3] != 0) anyAlpha = true;
            cols[i] = new Rgba(d[i * 4], d[i * 4 + 1], d[i * 4 + 2], d[i * 4 + 3]);
        }
        if (!anyAlpha)
            for (int i = 0; i < numCols; i++)
                cols[i] = cols[i] with { A = 255 };
        return cols;
    }

    /// <summary>
    /// Reads the sprite nodes and returns each one's on-disk link target, or -1.
    /// Links are turned into object references by ResolveLinks.
    /// </summary>
    private static int[] ReadSprites(SffFile f, byte[] b, Header h)
    {
        var links = new int[h.SprCount];
        var jobs = new List<Sprite>((int)Math.Min(h.SprCount, 1u << 20));
        for (int i = 0; i < h.SprCount; i++)
        {
            int off = (int)h.SprOffset + i * SpriteNodeSize;
            if (off + SpriteNodeSize > b.Length)
                throw new InvalidDataException($"sprite node {i} lies past the end of the file");
            var n = b.AsSpan(off, SpriteNodeSize);

            var s = new Sprite
            {
                Group = BinaryPrimitives.ReadUInt16LittleEndian(n),
                Index = BinaryPrimitives.ReadUInt16LittleEndian(n[2..]),
                Width = BinaryPrimitives.ReadUInt16LittleEndian(n[4..]),
                Height = BinaryPrimitives.ReadUInt16LittleEndian(n[6..]),
                AxisX = BinaryPrimitives.ReadInt16LittleEndian(n[8..]),
                AxisY = BinaryPrimitives.ReadInt16LittleEndian(n[10..]),
                SrcFormat = (SpriteFormat)n[14],
                SrcDepth = n[15],
            };
            int link = BinaryPrimitives.ReadUInt16LittleEndian(n[12..]);
            long dataOff = BinaryPrimitives.ReadUInt32LittleEndian(n[16..]);
            long dataLen = BinaryPrimitives.ReadUInt32LittleEndian(n[20..]);
            int palIdx = BinaryPrimitives.ReadUInt16LittleEndian(n[24..]);
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(n[26..]);

            if (palIdx >= 0 && palIdx < f.Palettes.Count) s.Palette = f.Palettes[palIdx];
            f.Sprites.Add(s);
            links[i] = -1;

            if (dataLen == 0) { links[i] = link; continue; } // borrows another node's pixels

            long start = ((flags & 1) != 0 ? h.TdataOffset : h.LdataOffset) + dataOff;
            if (start + dataLen > b.Length)
            {
                s.DecodeError = "sprite data lies past the end of the file";
                continue;
            }
            s.SourceData = b.AsSpan((int)start, (int)dataLen).ToArray();
            jobs.Add(s);
        }

        // Each sprite decodes independently from its own SourceData, so large files decode in parallel.
        Parallel.ForEach(jobs, s =>
        {
            try
            {
                Decoder.DecodePixels(s, s.SourceData!);
            }
            catch (Exception e)
            {
                s.DecodeError = e.Message;
            }
        });
        return links;
    }

    /// <summary>
    /// Turns on-disk link positions into references and copies the target's
    /// pixels across. Linked sprites keep their own group, index, axis and
    /// palette but share the target's bitmap.
    /// </summary>
    private static void ResolveLinks(SffFile f, int[] links)
    {
        for (int i = 0; i < links.Length; i++)
        {
            if (links[i] < 0) continue;
            var s = f.Sprites[i];
            if (links[i] >= f.Sprites.Count)
            {
                s.DecodeError = $"links to out-of-range sprite {links[i]}";
                continue;
            }
            var target = f.Sprites[links[i]];
            if (ReferenceEquals(target, s)) { s.DecodeError = "sprite links to itself"; continue; }
            s.SharesWith = target;
        }
        Sprite.BreakLinkLoops(f.Sprites);
        f.ForwardLinksOnLoad = [.. f.Sprites.Where((s, i) => s.SharesWith is not null && links[i] > i)];

        // Pixels and size are read through the link; this pass only carries a
        // failed target's error over to the sprites that borrow from it.
        for (int pass = 0; pass <= f.Sprites.Count; pass++)
        {
            bool done = true;
            foreach (var s in f.Sprites)
            {
                if (s.SharesWith is not { } src || s.HasPixels || s.DecodeError is not null) continue;

                if (src.DecodeError is not null)
                {
                    s.DecodeError = $"link target failed: {src.DecodeError}";
                }
                else done = false;
            }
            if (done) break;
        }
    }
}
