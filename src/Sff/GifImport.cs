using System.Security.Cryptography;

namespace Sff;

/// <summary>
/// Brings GIF animations in as sprites: one sprite per frame, in play order.
/// A GIF frame is usually only part of the picture, so frames are composited
/// onto the logical screen and every sprite comes out the same size.
/// </summary>
public static class GifImport
{
    /// <summary>
    /// Counts the frames without decoding pixels, so the editor can size its
    /// dialog before doing the work. Throws the same errors Decode would.
    /// </summary>
    public static int FrameCount(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!GifDecoder.LooksLikeGif(data)) throw new InvalidDataException("gif: bad signature");

        int p = 6;
        if (data.Length < p + 7) throw new InvalidDataException("gif: header is truncated");
        if ((data[p + 4] & 0x80) != 0) p += (2 << (data[p + 4] & 0x07)) * 3;
        p += 7; // past the logical screen descriptor, which the table follows

        int count = 0;
        while (p < data.Length)
        {
            byte block = data[p++];
            if (block == 0x3B) break;
            if (block == 0x21) // extension: label byte, then a sub-block chain
            {
                p++;
                p = SkipChain(data, p);
                continue;
            }
            if (block != 0x2C) break;
            if (p + 9 > data.Length) throw new InvalidDataException("gif: truncated image descriptor");
            byte flags = data[p + 8];
            p += 9;
            if ((flags & 0x80) != 0) p += (2 << (flags & 0x07)) * 3;
            p++; // LZW minimum code size
            p = SkipChain(data, p);
            count++;
        }
        if (count == 0) throw new InvalidDataException("gif: no frames");
        return count;
    }

    private static int SkipChain(byte[] data, int p)
    {
        while (p < data.Length)
        {
            int size = data[p++];
            if (size == 0) return p;
            p += size;
        }
        throw new InvalidDataException("gif: unterminated sub-block chain");
    }

    /// <summary>One frame of a decoded GIF, sized to the logical screen.</summary>
    public readonly record struct DecodedFrame(int Width, int Height, byte[] Bgra, int DelayCs);

    /// <summary>Colors one palette holds, not counting the transparency mask at index 0.</summary>
    public const int MaxSharedColors = 255;

    /// <summary>
    /// The colors a GIF's frames use between them: the union of their colors,
    /// in the order the colors first appear as the frames play. GIF's 256 is per
    /// color table rather than per file — a global table plus an optional local
    /// table per frame — so the frames can use different sets and the union can
    /// outgrow a palette. Overflows says when it has, and the caller decides what
    /// to do about it rather than having the answer picked for them.
    /// </summary>
    public sealed class GifColors
    {
        /// <summary>Index 0 is the transparency mask; the rest are the frames' colors.</summary>
        public Rgba[] Colors { get; }
        private readonly long[] _weights;

        public GifColors(Rgba[] colors, long[]? weights = null)
        {
            ArgumentNullException.ThrowIfNull(colors);
            if (colors.Length == 0) throw new ArgumentException("a palette needs a mask entry", nameof(colors));
            if (weights is not null && weights.Length != colors.Length)
                throw new ArgumentException("color and weight counts must match", nameof(weights));
            Colors = colors;
            _weights = weights ?? Enumerable.Repeat(1L, colors.Length).ToArray();
        }

        /// <summary>Colors the frames use between them, not counting the mask.</summary>
        public int ColorCount => Colors.Length - 1;

        /// <summary>True when the frames need more colors than one palette can hold.</summary>
        public bool Overflows => ColorCount > MaxSharedColors;

        /// <summary>The palette the frames share. Throws when they need too many colors for one.</summary>
        public Palette ToPalette() => Overflows
            ? throw new NotSupportedException(
                $"These frames use {ColorCount} colors between them, so they have no palette to keep. "
                + "Choose a palette to match them against, or import them as 32-bit color.")
            : ImageImport.PadTo256(new Palette { Colors = (Rgba[])Colors.Clone() });

        /// <summary>The shared palette cut down to fit, weighted by how often each color appears.</summary>
        public Palette Reduced(int maxColors = MaxSharedColors) => Quantize.Reduce(Colors, _weights, maxColors);
    }

    /// <summary>The colors a GIF's frames use between them, in play order.</summary>
    public static GifColors SharedColors(IReadOnlyList<DecodedFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var indices = new Dictionary<uint, int>();
        var colors = new List<Rgba> { new(0, 0, 0, 0) }; // index 0 stays the mask
        var weights = new List<long> { 0 };

        foreach (var f in frames)
            for (int i = 0; i + 3 < f.Bgra.Length; i += 4)
            {
                if (f.Bgra[i + 3] == 0) continue; // a transparent pixel carries no color
                byte b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                uint key = (uint)(r << 16 | g << 8 | b);
                if (indices.TryGetValue(key, out int index)) weights[index]++;
                else
                {
                    indices.Add(key, colors.Count);
                    colors.Add(new Rgba(r, g, b, 255));
                    weights.Add(1);
                }
            }
        return new GifColors([.. colors], [.. weights]);
    }

    /// <summary>Decodes every frame, composited onto the logical screen.</summary>
    public static List<DecodedFrame> DecodeFrames(byte[] data)
    {
        var frames = GifDecoder.Decode(data, out int width, out int height);
        var result = new List<DecodedFrame>(frames.Count);
        foreach (var f in frames)
            result.Add(new DecodedFrame(width, height, f.Bgra, f.DelayCs));
        return result;
    }
    /// <summary>
    /// Picks which frames to keep. Any frame whose pixels match an earlier kept
    /// frame is dropped, so a held pose and a frame that returns to an earlier
    /// image both go; the first frame is always kept.
    /// </summary>
    public static List<DecodedFrame> DropDuplicates(IReadOnlyList<DecodedFrame> frames) =>
        DropDuplicates(frames, pixels => BitConverter.ToInt32(SHA256.HashData(pixels)));

    internal static List<DecodedFrame> DropDuplicates(IReadOnlyList<DecodedFrame> frames, Func<byte[], int> digestOf)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(digestOf);
        var kept = new List<DecodedFrame>(frames.Count);
        var seen = new Dictionary<int, List<DecodedFrame>>();

        foreach (var frame in frames)
        {
            int digest = digestOf(frame.Bgra);
            if (!seen.TryGetValue(digest, out var bucket))
                seen.Add(digest, bucket = []);
            if (bucket.Any(previous => previous.Width == frame.Width
                && previous.Height == frame.Height && previous.Bgra.AsSpan().SequenceEqual(frame.Bgra)))
                continue;
            bucket.Add(frame);
            kept.Add(frame);
        }
        return kept;
    }

    /// <summary>How many frames DropDuplicates would remove.</summary>
    public static int DuplicateCount(IReadOnlyList<DecodedFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return frames.Count - DropDuplicates(frames).Count;
    }

    /// <summary>
    /// Builds one sprite per frame, in play order, through the same import plan
    /// a single image would use, so the dialog's palette and index choices
    /// apply here too. "The image's own palette" gives every frame one palette
    /// built from the colors the frames use between them, so a 20-frame GIF adds
    /// one palette rather than twenty; frames needing more than a palette holds
    /// throw, so ask first.
    /// Frames are re-encoded as PNGs only to reuse ImageImport's pixel
    /// handling; the plan decides the result. GIF transparency is written as
    /// alpha 0, and a GIF's alpha is only ever 0 or 255, so the transparent
    /// pixels are forced to color index 0 below whatever the nearest-color
    /// alpha threshold happens to be.
    /// </summary>
    public static List<Sprite> ToSprites(IReadOnlyList<DecodedFrame> frames, ImportPlan plan,
        out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return Build(frames, plan,
            plan.TrueColor || !plan.OwnPalette ? null : SharedColors(frames).ToPalette(), out reports);
    }

    /// <summary>
    /// Builds one sprite per frame against a palette they all share, for when
    /// the caller has already settled what to do about the frames' colors. The
    /// palette is used only when the plan asks for the image's own.
    /// </summary>
    public static List<Sprite> ToSprites(IReadOnlyList<DecodedFrame> frames, ImportPlan plan,
        Palette sharedPalette, out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(sharedPalette);
        return Build(frames, plan, sharedPalette, out reports);
    }

    private static List<Sprite> Build(IReadOnlyList<DecodedFrame> frames, ImportPlan plan,
        Palette? sharedPalette, out List<ImageImport.Report> reports)
    {
        ArgumentNullException.ThrowIfNull(frames);
        reports = new List<ImageImport.Report>(frames.Count);
        var sprites = new List<Sprite>(frames.Count);
        if (frames.Count == 0) return sprites;

        var shared = !plan.TrueColor && plan.OwnPalette ? sharedPalette : null;
        if (shared is not null) ImageImport.PadTo256(shared);
        foreach (var f in frames)
        {
            // Frames are re-encoded as PNGs only to reuse ImageImport's pixel
            // handling; the plan decides the result.
            var png = shared is { } pal
                ? PngExport.EncodeIndexed(SharedIndices(f, pal), f.Width, f.Height, pal.Colors)
                : PngExport.Encode(MarkGifTransparent(f.Bgra), f.Width, f.Height);

            sprites.Add(ImageImport.FromImage(png, plan, out var report));
            // An imported palette is copied out of the PNG, so point the sprite
            // at the one instance the frames share.
            if (shared is not null) sprites[^1].Palette = shared;
            reports.Add(report);
            if (plan.TrueColor) continue;

            // The threshold is a user setting for soft-edged images, and at 0 it
            // would not catch a hard GIF cutout, so the mask is applied again on
            // the built indexes.
            ForceTransparentIndexes(sprites[^1], f.Bgra, f.Width);
        }
        return sprites;
    }

    /// <summary>
    /// A frame's pixels as indexes into the shared palette. Every color the
    /// palette holds is found exactly, so only a palette cut down to fit
    /// approximates.
    /// </summary>
    private static byte[] SharedIndices(DecodedFrame f, Palette pal) =>
        ImageImport.MatchToPalette(MarkGifTransparent(f.Bgra), pal.Colors);

    /// <summary>
    /// Pins every fully transparent GIF pixel to the mask index, so transparency
    /// does not depend on the nearest-color alpha threshold.
    /// </summary>
    private static void ForceTransparentIndexes(Sprite sprite, byte[] bgra, int width)
    {
        if (sprite.Indexed is not { } indices) return;
        int count = Math.Min(indices.Length, bgra.Length / 4);
        for (int i = 0; i < count; i++)
        {
            if (bgra[i * 4 + 3] == 0) indices[i] = Sprite.TransparentIndex;
        }
    }

    /// <summary>Sets alpha 0 to fully transparent, and every other pixel fully opaque.</summary>
    private static byte[] MarkGifTransparent(byte[] bgra)
    {
        var copy = (byte[])bgra.Clone();
        for (int i = 0; i < copy.Length / 4; i++)
            copy[i * 4 + 3] = bgra[i * 4 + 3] == 0 ? (byte)0 : (byte)255;
        return copy;
    }
}
