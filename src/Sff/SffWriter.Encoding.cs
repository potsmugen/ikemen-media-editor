using System.Buffers.Binary;
using System.IO.Compression;

namespace Sff;

/// <summary>A sprite nobody edited that an SFF v2 save stores in its destination format. The pixels stay the same.</summary>
public readonly record struct SpriteReencode(Sprite Sprite, SpriteFormat From, int FromBytes, SpriteFormat To, int ToBytes);

/// <summary>
/// Sprite encodings worked out before an SFF v2 save (SffWriter.PlanEncoding), so the editor can show
/// progress. Valid for one save.
/// </summary>
public sealed class EncodingPlan
{
    internal EncodingPlan(bool allowPng) => AllowPng = allowPng;

    internal bool AllowPng { get; }

    internal Dictionary<Sprite, SffWriter.Encoded> Chosen { get; } = new();

    /// <summary>Untouched sprites the save re-encodes, in file order.</summary>
    public List<SpriteReencode> Reencoded { get; } = [];

    /// <summary>Encodings that did not decode back to the same pixels; the fallback format was used.</summary>
    public List<string> CheckFailures { get; } = [];

    /// <summary>Bytes the re-encoded sprites save; negative when they grew.</summary>
    public long BytesSaved => Reencoded.Sum(r => (long)r.FromBytes - r.ToBytes);
}

public static partial class SffWriter
{
    /// <summary>One sprite's stored bytes as written, length prefix included.</summary>
    internal readonly record struct Encoded(SpriteFormat Format, byte Depth, byte[] Bytes);

    /// <summary>
    /// The format an SFF v2 save stores a sprite in. 2.0.1.0: indexed PNG8; true color PNG24 when every
    /// pixel is opaque, else PNG32. 2.0.0.0: LZ5 at 5 bits and below, else RLE8. Never raw: it only wins on
    /// tiny sprites, and Ikemen GO refuses it below 8 bits (image.go readV2). LZ5 holds indices 0-31 only
    /// (the decoders mask colors with 0x1f), so it stays at 5 bits and below.
    /// </summary>
    internal static SpriteFormat Destination(Sprite s, bool allowPng)
    {
        if (s.Indexed is not null) return allowPng ? SpriteFormat.Png8 : IndexedDepth(s) <= 5 ? SpriteFormat.Lz5 : SpriteFormat.Rle8;
        return Opaque(s.Direct!) ? SpriteFormat.Png24 : SpriteFormat.Png32;
    }

    /// <summary>Each linked set's pixel owner, once, in file order: the sprites a v2 save encodes.</summary>
    private static List<Sprite> PixelOwners(SffFile f)
    {
        var seen = new HashSet<Sprite>();
        var owners = new List<Sprite>();
        foreach (var s in f.Sprites)
            if (seen.Add(PixelOwner(s))) owners.Add(PixelOwner(s));
        return owners;
    }

    /// <summary>Unedited sprites a v2 save in this version re-encodes (stored in another format than their
    /// destination), with that destination. Cheap: compares formats and scans alpha, encodes nothing.</summary>
    public static List<(Sprite Sprite, SpriteFormat To)> ReencodedOnSave(SffFile f, SffVersion version)
    {
        if (version.Hi == 1) return [];
        bool allowPng = SffVersions.SupportsPng(version);
        return [.. PixelOwners(f).Where(s => ChangesFormat(s, allowPng)).Select(s => (s, Destination(s, allowPng)))];
    }

    private static bool ChangesFormat(Sprite s, bool allowPng) =>
        s.HasPixels && Reuses(s, allowPng) && (s.Indexed is not null || allowPng) && s.SrcFormat != Destination(s, allowPng);

    /// <summary>The sprites PlanEncoding encodes: edited ones and those ReencodedOnSave lists. For a progress total.</summary>
    public static int EncodingCount(SffFile f, SffVersion version) => version.Hi == 1 ? 0 : ToEncode(f, SffVersions.SupportsPng(version)).Count;

    private static List<Sprite> ToEncode(SffFile f, bool allowPng) =>
        [.. PixelOwners(f).Where(s => s.HasPixels && (!Reuses(s, allowPng) || ChangesFormat(s, allowPng)))];

    /// <summary>
    /// Encodes every sprite a v2 save writes afresh, in parallel: edited ones, and untouched ones not in
    /// their destination format (listed in Reencoded). Progress counts sprites done. A v1 version gets
    /// an empty plan.
    /// </summary>
    public static EncodingPlan PlanEncoding(SffFile f, SffVersion version, IProgress<int>? progress = null,
        CancellationToken token = default)
    {
        bool allowPng = SffVersions.SupportsPng(version);
        var plan = new EncodingPlan(allowPng);
        if (version.Hi == 1) return plan;

        var work = ToEncode(f, allowPng);
        var results = new (Encoded? Enc, List<string>? Failures)[work.Count];
        int done = 0;
        Parallel.For(0, work.Count, new ParallelOptions { CancellationToken = token }, i =>
        {
            List<string>? failures = null;
            var enc = TryEncode(work[i], allowPng, ref failures);
            results[i] = (enc, failures);
            progress?.Report(Interlocked.Increment(ref done));
        });

        for (int i = 0; i < work.Count; i++)
        {
            var s = work[i];
            if (results[i].Failures is { } failures) plan.CheckFailures.AddRange(failures);
            if (results[i].Enc is not { } enc) continue; // invalid: Save refuses it with a message
            plan.Chosen[s] = enc;
            if (Reuses(s, allowPng))
                plan.Reencoded.Add(new SpriteReencode(s, s.SrcFormat, s.SourceData!.Length, enc.Format, enc.Bytes.Length));
        }
        return plan;
    }

    /// <summary>After a save with this plan, the session takes on the bytes written for re-encoded sprites,
    /// so the next save finds them in their destination format.</summary>
    public static void ApplyEncoding(EncodingPlan plan)
    {
        foreach (var r in plan.Reencoded)
        {
            var enc = plan.Chosen[r.Sprite];
            (r.Sprite.SourceData, r.Sprite.SrcFormat, r.Sprite.SrcDepth) = (enc.Bytes, enc.Format, enc.Depth);
        }
    }

    /// <summary>A sprite's destination encoding, or null when Save would refuse it (bad size, indices past
    /// its palette, true color below 2.0.1.0).</summary>
    private static Encoded? TryEncode(Sprite s, bool allowPng, ref List<string>? failures)
    {
        if (s.Indexed is not null)
            return TryIndexedDepth(s, out int depth) ? Encode(s, IndexedCandidates(s, depth, allowPng), ref failures) : null;
        if (allowPng && s.Direct is { } bgra && bgra.Length == s.Width * s.Height * 4)
            return Encode(s, DirectCandidates(s), ref failures);
        return null;
    }

    /// <summary>An indexed sprite's depth from its palette's color count; 8 without one or for an unsupported count.</summary>
    private static int IndexedDepth(Sprite s)
    {
        int colors = s.Palette?.Colors.Length ?? 256;
        return PaletteDepth.SupportedColorCounts.Contains(colors)
            ? Array.IndexOf(PaletteDepth.SupportedColorCounts, colors) + 1
            : 8;
    }

    /// <summary>The checks Save makes on an indexed sprite, without throwing.</summary>
    private static bool TryIndexedDepth(Sprite s, out int depth)
    {
        depth = IndexedDepth(s);
        var px = s.Indexed!;
        if (px.Length != s.Width * s.Height) return false;
        int max = 1 << depth;
        foreach (var v in px) if (v >= max) return false;
        return true;
    }

    private static bool Opaque(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 255) return false;
        return true;
    }

    /// <summary>The destination encoding first, then RLE8 as the fallback should it fail the check.</summary>
    private static IEnumerable<Encoded> IndexedCandidates(Sprite s, int depth, bool allowPng)
    {
        var px = s.Indexed!;
        byte d = (byte)depth;
        var dest = Destination(s, allowPng);
        if (dest == SpriteFormat.Png8)
            yield return new(SpriteFormat.Png8, d, Prefixed(px.Length,
                Png.EncodeIndexed(px, s.Width, s.Height, PngPalette(s, px), CompressionLevel.SmallestSize, alpha: false)));
        else if (dest == SpriteFormat.Lz5 && Lz5.CanEncode(px))
            yield return new(SpriteFormat.Lz5, d, Prefixed(px.Length, Lz5.Encode(px)));
        yield return new(SpriteFormat.Rle8, d, Prefixed(px.Length, Rle8.Encode(px)));
    }

    /// <summary>
    /// The PNG8 palette: the sprite's colors up to the highest index it uses. The engines read only the
    /// indices (mugen-unleashed Png_ReadStream, Ikemen image.Paletted.Pix), so the rest would be dead bytes.
    /// </summary>
    private static Rgba[] PngPalette(Sprite s, byte[] px)
    {
        int count = 1;
        foreach (var v in px) if (v >= count) count = v + 1;
        var colors = s.Palette?.Colors ?? [];
        var pal = new Rgba[count];
        for (int i = 0; i < count; i++) pal[i] = i < colors.Length ? colors[i] : new Rgba(0, 0, 0, 255);
        return pal;
    }

    /// <summary>The destination encoding first (PNG24 when opaque), then PNG32 as the fallback.</summary>
    private static IEnumerable<Encoded> DirectCandidates(Sprite s)
    {
        byte[] bgra = s.Direct!;
        if (Destination(s, allowPng: true) == SpriteFormat.Png24)
            yield return new(SpriteFormat.Png24, 24, Prefixed(s.Width * s.Height * 3, Png.EncodeRgb24(bgra, s.Width, s.Height)));
        yield return new(SpriteFormat.Png32, 32, Prefixed(bgra.Length, Png.Encode(bgra, s.Width, s.Height)));
    }

    /// <summary>
    /// The first candidate that decodes back to the sprite's pixels. One that does not is recorded and
    /// skipped, so an encoder bug never reaches the file.
    /// </summary>
    private static Encoded Encode(Sprite s, IEnumerable<Encoded> candidates, ref List<string>? failures)
    {
        foreach (var c in candidates)
        {
            if (DecodesBack(s, c)) return c;
            (failures ??= []).Add($"sprite {s.Group},{s.Index}: {c.Format} did not decode back to the same pixels; used the fallback format");
        }
        throw new InvalidDataException($"sprite {s.Group},{s.Index}: no format decodes back to the same pixels");
    }

    /// <summary>The destination encoding for a sprite Save encodes without a plan.</summary>
    private static Encoded EncodeForSave(Sprite s, IEnumerable<Encoded> candidates)
    {
        List<string>? failures = null;
        return Encode(s, candidates, ref failures);
    }

    private static bool DecodesBack(Sprite s, Encoded c)
    {
        var scratch = new Sprite { Width = s.Width, Height = s.Height, SrcFormat = c.Format, SrcDepth = c.Depth };
        try { Decoder.DecodePixels(scratch, c.Bytes); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
        if (s.Indexed is { } px) return scratch.Indexed is { } back && back.AsSpan().SequenceEqual(px);
        return scratch.Direct is { } got && SameDirect(s.Direct!, got);
    }

    /// <summary>BGRA equality where fully transparent pixels match whatever their color: the decoder zeroes it.</summary>
    private static bool SameDirect(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] != b[i + 3]) return false;
            if (a[i + 3] == 0) continue;
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return false;
        }
        return true;
    }

    private static byte[] Prefixed(int decompressedLength, byte[] body)
    {
        var data = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)decompressedLength);
        body.CopyTo(data, 4);
        return data;
    }
}
