using System.Buffers.Binary;

namespace Sff;

/// <summary>
/// Writes SFFv2. A sprite nobody has repainted is written back with its original
/// compressed bytes, so re-saving an untouched file reproduces its pixels exactly
/// (a file read from tdata is normalized to ldata on an ordinary save).
/// Repainted sprites are re-encoded; edited true-color sprites use the
/// smallest lossless raw or PNG payload. Sprite links and palettes are preserved.
/// Palettes always live in ldata; Save Like SprMaker 2.01 with
/// sprite.decompressonload puts sprite data in tdata with node flag 1.
/// </summary>
public static class SffWriter
{
    private record struct Placement(uint Offset, uint Length, SpriteFormat Format, byte Depth, bool InTdata);

    /// <summary>Writes the file in the requested version. clamp: group/number and
    /// axis values that don't fit are clamped to the field's range instead of wrapped.</summary>
    public static void Save(SffFile f, string path, SffVersion version, bool clamp = false)
    {
        if (version.Hi == 1) { SffV1Writer.Save(f, path, clamp); return; }
        Save(f, path, version.Lo2 >= 1 || version.Lo1 > 0, clamp);
    }

    /// <summary>Whether SFFv1 can hold every sprite (all 8-bit and decoded).</summary>
    public static bool FitsV1(SffFile f) => SffV1Writer.Unsupported(f, 1).Count == 0;

    public static void Save(SffFile f, string path) => Save(f, path, allowPng: true, clamp: false);

    /// <summary>
    /// Save Like SprMaker 2.01: 2.0.1.0, sprites encoded afresh according to
    /// the selected compression options.
    /// </summary>
    internal static void SaveLikeSprMaker2(SffFile f, string path, SprMaker2Options tool, bool clamp) =>
        Save(f, path, allowPng: true, clamp, tool);

    /// <summary>A u16 field: wrapped as before, or clamped to 0..65535.</summary>
    internal static ushort U16(int v, bool clamp) => clamp ? (ushort)Math.Clamp(v, 0, ushort.MaxValue) : unchecked((ushort)v);

    /// <summary>An s16 field: wrapped as before, or clamped to -32768..32767.</summary>
    internal static short S16(int v, bool clamp) => clamp ? (short)Math.Clamp(v, short.MinValue, short.MaxValue) : unchecked((short)v);

    /// <summary>Group a v1 palette gets on conversion to v2 when no sprite uses it,
    /// so there is no sprite group to take.</summary>
    public const int V1ConvertedPaletteGroup = 2;

    /// <summary>v1 → v2 keys for the SFF's own palettes. Sprite 0,0's palette takes 1,1
    /// unless another written palette (an .act) holds it; every other palette takes the
    /// group of the first sprite using it, numbered from 0 upward past taken keys.</summary>
    public static Dictionary<Palette, (int Group, int Index)> V1ConvertedKeys(SffFile f)
    {
        static bool Own(Palette p) => p.Source == PaletteSource.Embedded && p.SourcePath is null;
        var used = f.Sprites.Select(x => x.Palette).ToHashSet();
        var taken = f.Palettes.Where(p => !Own(p) && (p.Source == PaletteSource.Embedded || used.Contains(p)))
            .Select(p => (p.Group, p.Index)).ToHashSet();
        var keys = new Dictionary<Palette, (int, int)>();
        void Assign(Palette p, int group)
        {
            int index = 0;
            while (!taken.Add((group, index))) index++;
            keys[p] = (group, index);
        }

        if (f.Sprites.FirstOrDefault(x => x.Group == 0 && x.Index == 0)?.Palette is { } main
            && Own(main) && taken.Add((1, 1)))
            keys[main] = (1, 1);
        // Sprites that store pixels first; links only carry a palette when nothing else uses it.
        foreach (var s in f.Sprites.Where(x => x.SharesWith is null).Concat(f.Sprites.Where(x => x.SharesWith is not null)))
            if (s.Palette is { } p && Own(p) && !keys.ContainsKey(p)) Assign(p, s.Group);
        foreach (var p in f.Palettes)
            if (Own(p) && !keys.ContainsKey(p)) Assign(p, V1ConvertedPaletteGroup);
        return keys;
    }

    /// <summary>The external palettes a v1 → v2 save would drop (no sprite uses
    /// them), each with its group-1 key: the palette's own index, or the next
    /// free slot on conflict. Only externals a sprite uses occupy group 1 in
    /// that save; the SFF's own palettes take free keys after them (V1ConvertedKeys).</summary>
    public static List<(Palette Palette, int Index)> EmbeddableExternals(SffFile f)
    {
        var referenced = f.Sprites.Select(s => s.Palette).ToHashSet();
        var dropped = f.Palettes
            .Where(p => p.Source == PaletteSource.External && !referenced.Contains(p)).ToList();
        if (dropped.Count == 0) return [];

        var used = f.Palettes
            .Where(p => p.Source == PaletteSource.External && referenced.Contains(p))
            .Select(p => (p.Group, p.Index)).ToHashSet();
        var result = new List<(Palette, int)>(dropped.Count);
        foreach (var p in dropped)
        {
            int index = p.Index;
            while (!used.Add((1, index))) index++;
            result.Add((p, index));
        }
        return result;
    }

    private static void Save(SffFile f, string path, bool allowPng, bool clamp, SprMaker2Options? tool = null)
    {
        // Converting v1 to v2 rekeys the SFF's own palettes (V1ConvertedKeys).
        // v1-internal palettes have no source file; embedded .acts do.
        bool rekey = f.Version.Hi == 1;
        var converted = rekey ? V1ConvertedKeys(f) : null;

        // External palette files stay outside the SFF until at least one
        // sprite uses them. Once referenced, they are written as SFF palettes.
        var palettes = f.Palettes.Where(p => p.Source == PaletteSource.Embedded
            || f.Sprites.Any(s => ReferenceEquals(s.Palette, p))).ToList();
        // The converted table is written in key order.
        (int Group, int Index) WrittenKey(Palette p) =>
            converted is not null && converted.TryGetValue(p, out var key) ? key : (p.Group, p.Index);
        if (rekey) palettes = palettes.OrderBy(p => WrittenKey(p).Group).ThenBy(p => WrittenKey(p).Index).ToList();
        var palIdx = new Dictionary<Palette, int>();
        for (int i = 0; i < palettes.Count; i++) palIdx[palettes[i]] = i;

        // Links are held as references, so their on-disk positions are worked
        // out here, at the one moment the list order is fixed.
        var position = new Dictionary<Sprite, int>();
        for (int i = 0; i < f.Sprites.Count; i++) position[f.Sprites[i]] = i;

        var ldata = new MemoryStream();
        var tdata = new MemoryStream();
        var quad = new byte[4]; // reused so the loops below never allocate

        // A linked palette is written as a link to the palette at the end of its
        // chain (no data, that palette's number), if that one is in the file;
        // otherwise it is written in full.
        var palLink = new int[palettes.Count];
        for (int i = 0; i < palettes.Count; i++)
        {
            var root = palettes[i].LinksTo;
            while (root?.LinksTo is { } next) root = next;
            palLink[i] = root is not null && palIdx.TryGetValue(root, out int ri) ? ri : -1;
        }

        var palOff = new uint[palettes.Count];
        var palLen = new uint[palettes.Count];
        for (int i = 0; i < palettes.Count; i++)
        {
            if (palLink[i] >= 0) continue;
            palOff[i] = (uint)ldata.Length;
            foreach (var c in palettes[i].Colors)
            {
                (quad[0], quad[1], quad[2], quad[3]) = (c.R, c.G, c.B, c.A);
                ldata.Write(quad);
            }
            palLen[i] = (uint)ldata.Length - palOff[i];
        }

        var placed = new Placement?[f.Sprites.Count];

        // Save Like SprMaker 2.01 with sprite.decompressonload stores sprite data
        // in tdata with node flag 1; palettes always stay in ldata. Ordinary
        // saves never use tdata.
        bool toTdata = tool is not null && tool.DecompressOnLoad;

        for (int i = 0; i < f.Sprites.Count; i++)
        {
            var s = f.Sprites[i];
            if (s.SharesWith is not null) continue; // resolved once its target has a home

            // Links borrow their target's bytes with a zero length and carry no
            // flag of their own, so they never touch either block.
            var data = toTdata ? tdata : ldata;
            uint start = (uint)data.Length;
            SpriteFormat fmt;
            byte depth;

            // Save Like SprMaker 2.01 applies the selected compression to every pixel sprite.
            if (Reuses(s, allowPng) && !(tool is not null && (s.Indexed is not null || s.Direct is not null)))
            {
                data.Write(s.SourceData);
                (fmt, depth) = (s.SrcFormat, s.SrcDepth);
            }
            else if (s.Indexed is not null)
            {
                if (s.Indexed.Length != s.Width * s.Height)
                    throw new InvalidDataException(
                        $"sprite {i} has {s.Indexed.Length} pixels, expected {s.Width * s.Height}");

                int paletteColors = s.Palette?.Colors.Length ?? 256;
                int indexedDepth = PaletteDepth.SupportedColorCounts.Contains(paletteColors)
                    ? Array.IndexOf(PaletteDepth.SupportedColorCounts, paletteColors) + 1
                    : 8;
                if (s.Indexed.Any(index => index >= (1 << indexedDepth)))
                    throw new InvalidDataException(
                        $"sprite {i} uses indexes outside its {indexedDepth}-bit palette");
                if (tool is not null)
                    (fmt, depth) = EncodeLikeSprMaker2(data, quad, s, tool, indexedDepth);
                else if (indexedDepth == 5 && Lz5.CanEncode(s.Indexed))
                {
                    WriteLengthPrefixed(data, quad, (uint)s.Indexed.Length, Lz5.Encode(s.Indexed));
                    (fmt, depth) = (SpriteFormat.Lz5, 5);
                }
                else
                {
                    WriteLengthPrefixed(data, quad, (uint)s.Indexed.Length, Rle8.Encode(s.Indexed));
                    (fmt, depth) = (SpriteFormat.Rle8, (byte)indexedDepth);
                }
            }
            else if (s.Direct is not null)
            {
                if (!allowPng)
                    throw new NotSupportedException(
                        $"sprite {s.Group},{s.Index} is true color, which SFF 2.0.0.0 cannot store. "
                        + "Save as 2.0.1.0 instead.");
                if (s.Direct.Length != checked(s.Width * s.Height * 4))
                    throw new InvalidDataException(
                        $"sprite {s.Group},{s.Index} has {s.Direct.Length} true-color bytes, expected {s.Width * s.Height * 4}");

                if (tool is null)
                    (fmt, depth) = EncodeDirectOptimally(data, quad, s);
                else if (tool.Compress24 == SprMaker2Compress24.None)
                {
                    WriteRawRgb24(data, s.Direct);
                    (fmt, depth) = (SpriteFormat.Raw, 24);
                }
                else if (tool.Compress24 == SprMaker2Compress24.Png24)
                {
                    WriteLengthPrefixed(data, quad, (uint)(s.Width * s.Height * 3), Png.EncodeRgb24(s.Direct, s.Width, s.Height));
                    (fmt, depth) = (SpriteFormat.Png24, 24);
                }
                else if (tool.Compress24 == SprMaker2Compress24.Png32)
                {
                    WriteLengthPrefixed(data, quad, (uint)(s.Width * s.Height * 4), Png.Encode(s.Direct, s.Width, s.Height));
                    (fmt, depth) = (SpriteFormat.Png32, 32);
                }
                else throw new NotSupportedException($"sprite.compress.24 = {tool.Compress24}");
            }
            else throw new InvalidDataException($"sprite {i} has no pixel data to write");

            placed[i] = new Placement(start, (uint)data.Length - start, fmt, depth, toTdata);
        }

        ResolveLinkPlacements(f, placed, position);

        uint sprTable = SffReader.HeaderSize;
        uint palTable = sprTable + (uint)(f.Sprites.Count * SffReader.SpriteNodeSize);
        uint ldataStart = palTable + (uint)(palettes.Count * SffReader.PaletteNodeSize);
        uint tdataStart = ldataStart + (uint)ldata.Length;

        var head = new byte[ldataStart];
        SffReader.Signature.CopyTo(head);
        head[15] = 2;                          // version 2.0.0.0
        // Version is the caller's choice, never bumped: below 2.0.1.0, PNG-stored
        // sprites were re-encoded above (the editor asks first), true color throws.
        if (allowPng) head[13] = 1;
        head[27] = 2;              // compat, set as SprMaker 2.01 does: 2.0.1.0 only when a
        // sprite is written as PNG or deeper than 8 bits, else 2.0.0.0
        if (placed.Any(p => p is { } q && (IsPng(q.Format) || q.Depth > 8))) head[25] = 1;
        Put32(head, 36, sprTable);
        Put32(head, 40, (uint)f.Sprites.Count);
        Put32(head, 44, palTable);
        Put32(head, 48, (uint)palettes.Count);
        Put32(head, 52, ldataStart);
        Put32(head, 56, (uint)ldata.Length);
        Put32(head, 60, tdataStart);
        Put32(head, 64, (uint)tdata.Length);

        for (int i = 0; i < f.Sprites.Count; i++)
        {
            var s = f.Sprites[i];
            var pl = placed[i] ?? throw new InvalidDataException($"sprite {i} was never placed");
            int n = (int)sprTable + i * SffReader.SpriteNodeSize;

            Put16(head, n + 0, U16(s.Group, clamp));
            Put16(head, n + 2, U16(s.Index, clamp));
            Put16(head, n + 4, (ushort)s.Width);
            Put16(head, n + 6, (ushort)s.Height);
            Put16(head, n + 8, unchecked((ushort)S16(s.AxisX, clamp)));
            Put16(head, n + 10, unchecked((ushort)S16(s.AxisY, clamp)));
            if (s.SharesWith is not null) Put16(head, n + 12, (ushort)position[s.SharesWith]);
            head[n + 14] = (byte)pl.Format;
            head[n + 15] = pl.Depth;
            Put32(head, n + 16, pl.Offset);
            Put32(head, n + 20, pl.Length); // zero marks a linked sprite
            if (s.Palette is not null && palIdx.TryGetValue(s.Palette, out int pi))
                Put16(head, n + 24, (ushort)pi);
            if (pl.InTdata && pl.Length > 0) Put16(head, n + 26, 1);
        }

        for (int i = 0; i < palettes.Count; i++)
        {
            var p = palettes[i];
            int n = (int)palTable + i * SffReader.PaletteNodeSize;
            Put16(head, n + 0, U16(WrittenKey(p).Group, clamp));
            Put16(head, n + 2, U16(WrittenKey(p).Index, clamp));
            Put16(head, n + 4, (ushort)p.Colors.Length);
            if (palLink[i] is var link and >= 0)
            {
                Put16(head, n + 6, (ushort)link);
                Put32(head, n + 8, palOff[link]); // zero length marks the link
            }
            else
            {
                Put32(head, n + 8, palOff[i]);
                Put32(head, n + 12, palLen[i]);
            }
        }

        AtomicFile.Write(path, file =>
        {
            file.Write(head);
            ldata.Position = 0;
            ldata.CopyTo(file);
            tdata.Position = 0;
            tdata.CopyTo(file);
        });
    }

    /// <summary>
    /// A linked sprite points at its target's data and carries the target's
    /// format and depth, with a zero length marking it as a link. That is what
    /// real files do, rather than using the "linked" format id.
    /// </summary>
    private static void ResolveLinkPlacements(SffFile f, Placement?[] placed, Dictionary<Sprite, int> position)
    {
        for (int pass = 0; pass <= f.Sprites.Count; pass++)
        {
            bool done = true;
            for (int i = 0; i < f.Sprites.Count; i++)
            {
                var s = f.Sprites[i];
                if (s.SharesWith is not { } target || placed[i] is not null) continue;
                if (!position.TryGetValue(target, out int ti))
                    throw new InvalidDataException($"sprite {i} shares with a sprite that is not in this file");

                if (placed[ti] is { } t) placed[i] = t with { Length = 0 };
                else done = false;
            }
            if (done) break;
        }
    }

    /// <summary>
    /// SprMaker 2.01 derives indexed depth from palette length; each depth's
    /// sprite.compress option picks the format. Raw data has no length prefix.
    /// </summary>
    private static (SpriteFormat, byte) EncodeLikeSprMaker2(Stream ldata, byte[] quad, Sprite s,
        SprMaker2Options tool, int indexedDepth)
    {
        var px = s.Indexed!;
        int maxColors = 1 << indexedDepth;
        if (px.Any(index => index >= maxColors))
            throw new InvalidDataException(
                $"sprite {s.Group},{s.Index} uses indexes outside its {indexedDepth}-bit palette");

        if (indexedDepth == 5)
        {
            switch (tool.Compress5)
            {
                case SprMaker2Compress5.None: ldata.Write(px); return (SpriteFormat.Raw, 5);
                case SprMaker2Compress5.Lz5:
                    WriteLengthPrefixed(ldata, quad, (uint)px.Length, Lz5.Encode(px));
                    return (SpriteFormat.Lz5, 5);
                case SprMaker2Compress5.Rle5:
                    WriteLengthPrefixed(ldata, quad, (uint)px.Length, Rle5.Encode(px));
                    return (SpriteFormat.Rle5, 5);
                default: throw new NotSupportedException($"sprite.compress.5 = {tool.Compress5}");
            }
        }

        switch (tool.Compress8)
        {
            case SprMaker2Compress8.None: ldata.Write(px); return (SpriteFormat.Raw, (byte)indexedDepth);
            case SprMaker2Compress8.Rle8:
                WriteLengthPrefixed(ldata, quad, (uint)px.Length, Rle8.Encode(px));
                return (SpriteFormat.Rle8, (byte)indexedDepth);
            default: throw new NotSupportedException($"sprite.compress.8 = {tool.Compress8}");
        }
    }

    /// <summary>Selects the shortest lossless representation for a changed true-color sprite.</summary>
    private static (SpriteFormat, byte) EncodeDirectOptimally(Stream ldata, byte[] quad, Sprite s)
    {
        byte[] bgra = s.Direct!;
        bool opaque = true;
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 255) { opaque = false; break; }

        byte[] png32 = Png.Encode(bgra, s.Width, s.Height);
        int png32Length = checked(png32.Length + 4);
        if (!opaque)
        {
            if (bgra.Length <= png32Length)
            {
                WriteRawRgba(ldata, bgra);
                return (SpriteFormat.Raw, 32);
            }
            WriteLengthPrefixed(ldata, quad, (uint)bgra.Length, png32);
            return (SpriteFormat.Png32, 32);
        }

        byte[] png24 = Png.EncodeRgb24(bgra, s.Width, s.Height);
        int png24Length = checked(png24.Length + 4);
        int raw24Length = checked(s.Width * s.Height * 3);
        if (raw24Length <= png24Length && raw24Length <= png32Length)
        {
            WriteRawRgb24(ldata, bgra);
            return (SpriteFormat.Raw, 24);
        }
        if (png24Length <= png32Length)
        {
            WriteLengthPrefixed(ldata, quad, (uint)raw24Length, png24);
            return (SpriteFormat.Png24, 24);
        }
        WriteLengthPrefixed(ldata, quad, (uint)bgra.Length, png32);
        return (SpriteFormat.Png32, 32);
    }

    private static void WriteRawRgb24(Stream ldata, byte[] bgra)
    {
        var rgb = new byte[checked(bgra.Length / 4 * 3)];
        for (int src = 0, dst = 0; src < bgra.Length; src += 4)
        {
            rgb[dst++] = bgra[src + 2];
            rgb[dst++] = bgra[src + 1];
            rgb[dst++] = bgra[src];
        }
        ldata.Write(rgb);
    }

    private static void WriteRawRgba(Stream ldata, byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        ldata.Write(rgba);
    }

    private static bool IsPng(SpriteFormat f) => f is SpriteFormat.Png8 or SpriteFormat.Png24 or SpriteFormat.Png32;

    /// <summary>Whether a sprite's original on-disk bytes are written back as they are.</summary>
    private static bool Reuses(Sprite s, bool allowPng) =>
        s.SharesWith is null && !s.PixelsDirty && s.SourceData is { Length: > 0 } && (allowPng || !IsPng(s.SrcFormat));

    /// <summary>Indexed sprites still stored as PNG from the original file: saving below 2.0.1.0 re-encodes them.</summary>
    public static int StoredAsPng(SffFile f) =>
        f.Sprites.Count(s => s.SharesWith is null && !s.PixelsDirty && s.SourceData is { Length: > 0 }
            && IsPng(s.SrcFormat) && s.Indexed is not null);

    /// <summary>Compressed payloads are prefixed with their decompressed length.</summary>
    private static void WriteLengthPrefixed(Stream s, byte[] scratch, uint decompressedLength, ReadOnlySpan<byte> body)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, decompressedLength);
        s.Write(scratch);
        s.Write(body);
    }

    private static void Put16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    private static void Put32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
}
