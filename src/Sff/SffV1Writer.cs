using System.Buffers.Binary;

namespace Sff;

/// <summary>
/// Writes SFFv1: a 512-byte header followed by a linked list of 32-byte
/// subheaders, each carrying a PCX image. Palettes travel inside the PCX, and
/// subheader flags let sprites reuse the preceding palette.
/// </summary>
internal static class SffV1Writer
{
    private const int HeaderSize = 512;
    private const int SubHeaderSize = 32;

    public static void Save(SffFile f, string path, bool clamp = false, bool linkDuplicatePalettes = false, bool usePreviousPalette = false, List<SprMakerEvent>? events = null)
    {
        var unsupported = Unsupported(f, 5);
        if (unsupported.Count > 0)
            throw new NotSupportedException(
                "SFFv1 holds 8-bit sprites only, but these are true color or undecoded: "
                + string.Join(", ", unsupported) + ". Save as SFF 2.0.1.0 instead.");

        var body = new MemoryStream();
        var offsets = new int[f.Sprites.Count];
        var position = new Dictionary<Sprite, int>();
        for (int i = 0; i < f.Sprites.Count; i++) position[f.Sprites[i]] = i;

        // Two passes: lay the subfiles out, then fill in the next pointers once
        // every offset is known.
        var payloads = new byte[f.Sprites.Count][];
        var samePalettes = new bool[f.Sprites.Count];
        Palette? previousPalette = null;
        Sprite? previousSprite = null;

        for (int i = 0; i < f.Sprites.Count; i++)
        {
            var s = f.Sprites[i];
            if (s.SharesWith is not null)
            {
                samePalettes[i] = usePreviousPalette && previousPalette is not null;
                payloads[i] = [];
                continue;
            }

            var pal = s.Palette ?? new Palette { Colors = [] };
            bool samePalette = previousPalette is not null
                && (usePreviousPalette || ReferenceEquals(pal, previousPalette));
            if (linkDuplicatePalettes && previousPalette is not null)
                samePalette |= PcxWriter.SameRgbPalette(pal.Colors, previousPalette.Colors);
            samePalettes[i] = samePalette;
            if (samePalette && events is not null && previousSprite is not null)
                events.Add(new SprMakerEvent(SprMakerEventKind.PaletteReused,
                    s.Group, s.Index, previousSprite.Group, previousSprite.Index));
            payloads[i] = PcxWriter.Encode(s.Indexed!, s.Width, s.Height, pal.Colors, !samePalette);
            previousPalette = pal;
            previousSprite = s;
        }

        int offset = HeaderSize;
        for (int i = 0; i < f.Sprites.Count; i++)
        {
            offsets[i] = offset;
            offset += SubHeaderSize + payloads[i].Length;
        }

        for (int i = 0; i < f.Sprites.Count; i++)
        {
            var s = f.Sprites[i];
            var n = new byte[SubHeaderSize];
            bool last = i == f.Sprites.Count - 1;

            // The last one points at the end of the file, as SprMaker 0.97 writes it.
            BinaryPrimitives.WriteUInt32LittleEndian(n, last ? (uint)offset : (uint)offsets[i + 1]);
            BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(4), (uint)payloads[i].Length);
            BinaryPrimitives.WriteInt16LittleEndian(n.AsSpan(8), SffWriter.S16(s.AxisX, clamp));
            BinaryPrimitives.WriteInt16LittleEndian(n.AsSpan(10), SffWriter.S16(s.AxisY, clamp));
            BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(12), SffWriter.U16(s.Group, clamp));
            BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(14), SffWriter.U16(s.Index, clamp));
            if (s.SharesWith is not null)
                BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(16), (ushort)position[s.SharesWith]);

            n[18] = (byte)(samePalettes[i] ? 1 : 0);

            body.Write(n);
            body.Write(payloads[i]);
        }

        var header = new byte[HeaderSize];
        SffReader.Signature.CopyTo(header);
        header[12] = 0; header[13] = 1; header[14] = 0; header[15] = 1; // version 1.0.1.0
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)CountGroups(f, clamp));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)f.Sprites.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), SubHeaderSize);
        header[32] = (byte)(usePreviousPalette ? 1 : 0); // palette type 2: use previous sprite's palette
        // Bytes 36-511 stay zero: SprMaker 0.97 clears them and writes nothing there.

        AtomicFile.Write(path, file =>
        {
            file.Write(header);
            body.Position = 0;
            body.CopyTo(file);
        });
    }

    /// <summary>Up to <paramref name="max"/> sprites SFFv1 cannot hold (true color or undecoded), as "group,index".</summary>
    internal static List<string> Unsupported(SffFile f, int max) => f.Sprites
        .Where(s => s.SharesWith is null && s.Indexed is null)
        .Select(s => $"{s.Group},{s.Index}")
        .Take(max)
        .ToList();

    private static int CountGroups(SffFile f, bool clamp) => f.Sprites.Select(s => SffWriter.U16(s.Group, clamp)).Distinct().Count();
}
