using System.Text.Json.Serialization;

namespace Sff;

/// <summary>sprite.compress.5: how SprMaker 2.01 stores 5-bit sprites.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SprMaker2Compress5>))]
public enum SprMaker2Compress5 { Lz5, Rle5, None }

/// <summary>sprite.compress.8: how SprMaker 2.01 stores 8-bit sprites.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SprMaker2Compress8>))]
public enum SprMaker2Compress8 { Rle8, None }

/// <summary>sprite.compress.24: how true-color sprites are stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SprMaker2Compress24>))]
public enum SprMaker2Compress24 { None, Png24, Png32 }

/// <summary>
/// SprMaker 0.97 (SFFv1) save options, named after its switches. Defaults
/// are the tool's with no switches. Options the writer can't do yet are
/// listed by Unsupported() and refused by SprMakerSave.
/// </summary>
public sealed class SprMakerOptions
{
    /// <summary>-c: crop every image to its content, moving the axis with it.</summary>
    public bool AutoCrop { get; set; }

    /// <summary>-f: link sprites whose PCX (pixels and palette) is identical.</summary>
    public bool LinkDuplicateSprites { get; set; }

    /// <summary>-p: reuse the previous PCX palette when the serialized RGB tables match.</summary>
    public bool LinkDuplicatePalettes { get; set; }

    /// <summary>Palette type 2: sprites reuse the preceding sprite's palette.</summary>
    [JsonPropertyName("SharedPalette")]
    public bool UsePreviousPalette { get; set; }

    /// <summary>-s: the report file holds only the summary and errors.</summary>
    public bool SummaryMode { get; set; }

    /// <summary>-q: no report file is written.</summary>
    public bool QuietMode { get; set; }

    public SprMakerOptions Copy() => (SprMakerOptions)MemberwiseClone();

    /// <summary>Names of the options set to something the writer can't do yet.</summary>
    public List<string> Unsupported()
    {
        var list = new List<string>();
        if (LinkDuplicatePalettes && !SupportsLinkDuplicatePalettes) list.Add("-p (link duplicate palettes)");
        if (UsePreviousPalette && !SupportsUsePreviousPalette) list.Add("palette type 2 (use previous sprite's palette)");
        return list;
    }

    // Writer support, for the dialog to enable controls from.
    public static bool SupportsLinkDuplicatePalettes => true;
    public static bool SupportsUsePreviousPalette => true;
}

/// <summary>
/// SprMaker 2.01 (SFFv2) save options, named after its [Option] keys. Defaults are
/// the tool's with no switches, where known (its txt2def template turns
/// autocrop and palette dedupe on; the built-in defaults are unverified).
/// Compress24 defaults to Png32; the built-in default is unverified.
/// </summary>
public sealed class SprMaker2Options
{
    /// <summary>-c / sprite.autocrop.</summary>
    public bool AutoCrop { get; set; }

    /// <summary>-f / sprite.detectduplicates: link identical source images.</summary>
    public bool DetectDuplicateSprites { get; set; }

    /// <summary>-p / pal.detectduplicates: find identical palettes.</summary>
    public bool DetectDuplicatePalettes { get; set; }

    /// <summary>pal.discardduplicates: drop identical palettes instead of linking them.</summary>
    public bool DiscardDuplicatePalettes { get; set; }

    public SprMaker2Compress5 Compress5 { get; set; } = SprMaker2Compress5.Lz5;
    public SprMaker2Compress8 Compress8 { get; set; } = SprMaker2Compress8.Rle8;
    public SprMaker2Compress24 Compress24 { get; set; } = SprMaker2Compress24.Png32;

    /// <summary>sprite.decompressonload: store sprites for decompressing once at load.</summary>
    public bool DecompressOnLoad { get; set; }

    /// <summary>-v: the report file also lists every linked, cropped and changed palette line.</summary>
    public bool VerboseMode { get; set; }

    /// <summary>-V: the report file lists everything, not just links, crops and palettes.</summary>
    public bool VeryVerboseMode { get; set; }

    /// <summary>-q / -Q: no report file is written (warnings and errors still reach the status bar).</summary>
    public bool QuietMode { get; set; }

    public SprMaker2Options Copy() => (SprMaker2Options)MemberwiseClone();

    /// <summary>Names of the options set to something the writer can't do yet.</summary>
    public List<string> Unsupported()
    {
        var list = new List<string>();
        if (!Supports(Compress5)) list.Add($"sprite.compress.5 = {Compress5}");
        if (!Supports(Compress8)) list.Add($"sprite.compress.8 = {Compress8}");
        if (!Supports(Compress24)) list.Add($"sprite.compress.24 = {Compress24}");
        if (DecompressOnLoad && !SupportsDecompressOnLoad) list.Add("sprite.decompressonload");
        if (VeryVerboseMode && !SupportsVeryVerbose) list.Add("-V (very verbose)");
        return list;
    }

    // Writer support, for the dialog to enable controls from.
    public static bool SupportsPaletteDedupe => true;
    public static bool SupportsDecompressOnLoad => true;
    public static bool SupportsVeryVerbose => true;
    public static bool Supports(SprMaker2Compress5 c) => c is SprMaker2Compress5.Lz5 or SprMaker2Compress5.Rle5 or SprMaker2Compress5.None;
    public static bool Supports(SprMaker2Compress8 c) => c is SprMaker2Compress8.Rle8 or SprMaker2Compress8.None;
    public static bool Supports(SprMaker2Compress24 c) => c is SprMaker2Compress24.None or SprMaker2Compress24.Png24 or SprMaker2Compress24.Png32;
}

/// <summary>What changed in a SprMaker-style save, for the save report.</summary>
public enum SprMakerEventKind { SpriteCropped, SpriteLinked, CropFollower, PaletteLinked, PaletteDiscarded, PaletteReused }

/// <summary>One reported change: its sprite or palette key plus what moved and by how much.</summary>
public sealed record SprMakerEvent(SprMakerEventKind Kind, int Group, int Index,
    int TargetGroup = -1, int TargetIndex = -1,
    int OldWidth = 0, int OldHeight = 0, int NewWidth = 0, int NewHeight = 0,
    int AxisDx = 0, int AxisDy = 0);

/// <summary>One source sprite a very-verbose save reports reading: the tool logs
/// a palette line per source file. Keyed like every other report line.</summary>
public sealed record SprMakerRead(int Group, int Index, bool HasPalette);

/// <summary>What a SprMaker-style save changed in the written file. Reads is the
/// per-source list the very-verbose report needs and is not part of equality.</summary>
public sealed record SprMakerReport(
    IReadOnlyList<SprMakerEvent> Events,
    IReadOnlyList<SprMakerRead>? Reads = null)
{
    public int Cropped => Events.Count(e => e.Kind == SprMakerEventKind.SpriteCropped);
    public int Linked => Events.Count(e => e.Kind == SprMakerEventKind.SpriteLinked);
    public int Palettes => Events.Count(e => e.Kind is SprMakerEventKind.PaletteLinked or SprMakerEventKind.PaletteDiscarded);
    public int PalettesDiscarded => Events.Count(e => e.Kind == SprMakerEventKind.PaletteDiscarded);
    public int AxesFollowed => Events.Count(e => e.Kind == SprMakerEventKind.CropFollower);
    public bool Equals(SprMakerReport? other) => other is not null && Events.SequenceEqual(other.Events);
    public override int GetHashCode() => Events.Aggregate(0, (h, e) => HashCode.Combine(h, e));

    /// <summary>
    /// The report as plain tool-style text: with verbose lines one line per
    /// change in file order, otherwise only the summary block. Very verbose adds
    /// the tool's per-source lines before them. SprMaker 0.97 and 2.01 each have
    /// their own lines.
    /// </summary>
    public string ToText(bool v2, bool verbose, bool veryVerbose = false)
    {
        var lines = new List<string>();
        if (veryVerbose)
            foreach (var r in Reads ?? [])
            {
                lines.Add($"Auto pal for ({r.Group},{r.Index})");
                if (r.HasPalette) lines.Add($"Reading palette from ({r.Group},{r.Index}).");
            }
        if (verbose || veryVerbose)
            foreach (var e in Events)
            {
                lines.Add(e.Kind switch
                {
                    SprMakerEventKind.SpriteCropped when v2 =>
                        $"Autocrop sprite ({e.Group},{e.Index}) {e.OldWidth}x{e.OldHeight} to {e.NewWidth}x{e.NewHeight}",
                SprMakerEventKind.SpriteCropped =>
                    $"({e.Group},{e.Index}) cropped {e.OldWidth},{e.OldHeight} -> {e.NewWidth},{e.NewHeight}",
                SprMakerEventKind.SpriteLinked when v2 =>
                    $"Linking sprite ({e.Group},{e.Index}): same as ({e.TargetGroup},{e.TargetIndex})",
                SprMakerEventKind.SpriteLinked =>
                    $"Sprite ({e.Group},{e.Index}) linked to ({e.TargetGroup},{e.TargetIndex})",
                SprMakerEventKind.PaletteLinked when v2 =>
                    $"Linking palette ({e.Group},{e.Index}): same as ({e.TargetGroup},{e.TargetIndex})",
                SprMakerEventKind.PaletteDiscarded when v2 =>
                    $"Discarding palette ({e.Group},{e.Index}): same as ({e.TargetGroup},{e.TargetIndex})",
                SprMakerEventKind.PaletteReused =>
                    $"Duplicate pal eliminated ({e.Group},{e.Index}): same as ({e.TargetGroup},{e.TargetIndex})",
                _ => $"Sprite ({e.Group},{e.Index}) axis followed to ({e.TargetGroup},{e.TargetIndex})",
                });
            }
        if (verbose || veryVerbose) lines.Add("");
        lines.Add(Summary(v2));
        return string.Join(Environment.NewLine, lines);
    }

    private string Summary(bool v2)
    {
        if (v2)
        {
            var lines = new List<string>();
            if (Linked > 0) lines.Add($"{Linked} duplicate sprite(s) removed");
            if (Cropped > 0) lines.Add($"{Cropped} sprite(s) cropped");
            if (Palettes > 0) lines.Add($"{Palettes} duplicate palette(s) {(PalettesDiscarded > 0 ? "discarded" : "linked")}");
            if (lines.Count == 0) lines.Add("No sprites or palettes changed");
            return string.Join(Environment.NewLine, lines);
        }
        return $"Total number of unique images:  {Cropped} cropped, {Linked} linked";
    }
}

/// <summary>
/// Saves the way SprMaker 0.97 / SprMaker 2.01 would build the file. Options apply to a
/// copy, so the open file is never changed.
/// </summary>
public static class SprMakerSave
{
    /// <summary>SprMaker 0.97: SFFv1.</summary>
    public static SprMakerReport Save(SffFile file, string path, SprMakerOptions o, bool clamp = false)
    {
        Refuse(o.Unsupported());
        var copy = file.Clone();
        var events = new List<SprMakerEvent>();
        LinkSprites(copy, v1: true, link: o.LinkDuplicateSprites, events);
        CropSprites(copy, crop: o.AutoCrop, events);
        var reused = new List<SprMakerEvent>();
        SffV1Writer.Save(copy, path, clamp, o.LinkDuplicatePalettes, o.UsePreviousPalette, reused);
        events.AddRange(reused);
        return new SprMakerReport(events);
    }

    /// <summary>SprMaker 2.01: SFFv2, written as 2.0.1.0 like the tool.</summary>
    public static SprMakerReport Save(SffFile file, string path, SprMaker2Options o, bool clamp = false)
    {
        Refuse(o.Unsupported());
        var copy = file.Clone();
        var events = new List<SprMakerEvent>();
        LinkSprites(copy, v1: false, link: o.DetectDuplicateSprites, events);
        LinkPalettes(copy, o.DetectDuplicatePalettes || o.DiscardDuplicatePalettes, o.DiscardDuplicatePalettes, events);
        CropSprites(copy, crop: o.AutoCrop, events);
        SffWriter.SaveLikeSprMaker2(copy, path, o, clamp);
        return new SprMakerReport(events, ReadSources(file));
    }

    // The very-verbose report logs each source it read. The editor saves from an
    // open SFF, so the sources are its sprites; one with pixels or a palette is one
    // the tool would have read a palette from.
    private static List<SprMakerRead> ReadSources(SffFile file) =>
        file.Sprites.Select(s => new SprMakerRead(s.Group, s.Index, s.Palette is not null || s.Indexed is not null)).ToList();

    private static void Refuse(List<string> unsupported)
    {
        if (unsupported.Count > 0)
            throw new NotSupportedException("Not supported yet: " + string.Join(", ", unsupported) + ".");
    }

    private static void LinkSprites(SffFile f, bool v1, bool link, List<SprMakerEvent> events)
    {
        if (!link) return;
        var sets = f.FindDuplicateSprites(v1);
        foreach (var set in sets)
        {
            var root = set[0];
            foreach (var s in set.Skip(1))
            {
                if (!ReferenceEquals(s.SharesWith, root))
                    events.Add(new SprMakerEvent(SprMakerEventKind.SpriteLinked, s.Group, s.Index, root.Group, root.Index));
            }
        }
        f.ShareDuplicateSprites(sets, v1);
    }

    private static void LinkPalettes(SffFile f, bool detect, bool discard, List<SprMakerEvent> events)
    {
        if (!detect && !discard) return;
        var kind = discard ? SprMakerEventKind.PaletteDiscarded : SprMakerEventKind.PaletteLinked;
        var sets = f.FindDuplicatePalettes(ignoreTransparentMaskRgb: false);
        foreach (var set in sets)
        {
            var keep = set[0];
            foreach (var p in set.Skip(1))
            {
                if (!ReferenceEquals(p.LinksTo, keep))
                    events.Add(new SprMakerEvent(kind, p.Group, p.Index, keep.Group, keep.Index));
            }
        }
        f.ShareDuplicatePalettes(sets, discard: discard);
    }

    // Links first, so a crop runs once per shared image and its borrowers follow.
    private static void CropSprites(SffFile f, bool crop, List<SprMakerEvent> events)
    {
        if (!crop) return;
        foreach (var s in f.Sprites)
        {
            if (s.SharesWith is not null) continue;
            int oldW = s.Width, oldH = s.Height;
            var r = SpriteCrop.Crop(s);
            if (!r.Changed) continue;
            events.Add(new SprMakerEvent(SprMakerEventKind.SpriteCropped,
                s.Group, s.Index, OldWidth: oldW, OldHeight: oldH, NewWidth: s.Width, NewHeight: s.Height,
                AxisDx: -r.Left, AxisDy: -r.Top));
            foreach (var b in f.Sprites)
                if (ReferenceEquals(b.SharesWith, s))
                {
                    b.AxisX -= r.Left;
                    b.AxisY -= r.Top;
                    events.Add(new SprMakerEvent(SprMakerEventKind.CropFollower,
                        b.Group, b.Index, s.Group, s.Index, AxisDx: -r.Left, AxisDy: -r.Top));
                }
        }
    }
}
