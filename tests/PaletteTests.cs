using System.Text;
using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Palette choice on import (own / given, kept indexes / nearest), and the
/// palette-run operations — MakePaletteUnique / UsePreviousPalette — whose v1
/// run-follows-pointer rule is the whole point.
/// </summary>
public class PaletteTests
{
    private static Rgba C(byte v) => new(v, v, v, 255);

    // ---- import plans ------------------------------------------------------

    [Fact]
    public void KeptIndexesLandInTheGivenPalette()
    {
        // An indexed PNG whose palette is nothing like the target's.
        var plte = new Rgba[] { new(0, 0, 0, 0), C(1), C(2), C(3) };
        var target = new Palette { Colors = [new(0, 0, 0, 0), C(9), C(8), C(7)] };
        var png = PngExport.EncodeIndexed([0, 2, 1, 3], 2, 2, plte);

        var sprite = ImageImport.FromImage(png, ImportPlan.Given(target, keepIndexes: true), out var report);

        Assert.Same(target, sprite.Palette);
        Assert.Equal([0, 2, 1, 3], sprite.Indexed);  // indexes pass through untouched
        Assert.Equal(0, report.OutOfRange);
        Assert.True(report.Lossless);
    }

    [Fact]
    public void KeptIndexesBeyondThePaletteAreReported()
    {
        var plte = new Rgba[] { new(0, 0, 0, 0), C(1), C(2), C(3) };
        var target = new Palette { Colors = [new(0, 0, 0, 0), C(9)] };  // two colors only
        var png = PngExport.EncodeIndexed([0, 1, 2, 3], 2, 2, plte);

        var sprite = ImageImport.FromImage(png, ImportPlan.Given(target, keepIndexes: true), out var report);

        Assert.Equal([0, 1, 2, 3], sprite.Indexed);
        Assert.Equal(2, report.OutOfRange);  // indexes 2 and 3 have no color to show
        Assert.False(report.Lossless);
    }

    [Fact]
    public void RemapIndexesUsesNearestColorAndKeepsMaskTransparency()
    {
        var source = new Palette
        {
            Colors = [new(0, 0, 0, 255), C(10), C(200), new(40, 40, 40, 0)],
        };
        var target = new Rgba[] { new(0, 0, 0, 255), C(0), C(100), C(255) };

        var mapped = ImageImport.RemapIndexes([0, 1, 2, 3], source, target);

        Assert.Equal([0, 1, 3, 0], mapped);
    }

    [Fact]
    public void RemapIndexesRejectsIndexesMissingFromSourcePalette()
    {
        var source = new Palette { Colors = [new(0, 0, 0, 0), C(10)] };

        var error = Assert.Throws<InvalidDataException>(() =>
            ImageImport.RemapIndexes([2], source, [new(0, 0, 0, 0), C(10)]));

        Assert.Contains("has no color", error.Message);
    }

    [Fact]
    public void MatchToPaletteConvertsBgraToNearestIndexes()
    {
        // BGRA: black, white, and a fully transparent pixel.
        byte[] bgra = [0, 0, 0, 255, 250, 250, 250, 255, 9, 9, 9, 0];
        var target = new Rgba[] { new(0, 0, 0, 0), C(0), C(255) };

        var mapped = ImageImport.MatchToPalette(bgra, target);

        Assert.Equal([1, 2, 0], mapped);
    }

    [Fact]
    public void MatchToPaletteUsesTheTransparentAlphaThreshold()
    {
        // Alpha below the default threshold (32) is transparent; at it, opaque.
        byte[] bgra = [0, 0, 0, 31, 0, 0, 0, 32];
        var target = new Rgba[] { new(0, 0, 0, 0), C(0), C(255) };

        var mapped = ImageImport.MatchToPalette(bgra, target);

        Assert.Equal([0, 1], mapped);
    }

    [Fact]
    public void ReplacingIndexedPngPalettePreservesImageIndexes()
    {
        byte[] indexes = [0, 1, 2, 1];
        var original = new Rgba[] { new(0, 0, 0, 0), C(20), C(40) };
        var replacement = new Palette { Colors = [new(0, 0, 0, 0), C(80), C(120)] };
        byte[] source = PngExport.EncodeIndexed(indexes, 2, 2, original);

        byte[] changed = PngExport.ReplacePalette(source, replacement);
        var decoded = PngExport.DecodeToBgra(changed);
        var expected = new byte[]
        {
            0, 0, 0, 0, 80, 80, 80, 255,
            120, 120, 120, 255, 80, 80, 80, 255,
        };

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(expected, decoded.Bgra);
    }

    [Fact]
    public void ReplacingPcxPalettePreservesImageIndexes()
    {
        byte[] indexes = [0, 1, 2, 1];
        var original = new Palette { Colors = [new(0, 0, 0, 0), C(20), C(40)] };
        var replacement = new Palette { Colors = [new(0, 0, 0, 0), C(80), C(120)] };
        byte[] source = PcxExport.EncodeIndexed(indexes, 2, 2, original);

        byte[] changed = PcxExport.ReplacePalette(source, replacement);
        var decoded = ImageImport.FromImage(changed, ImportPlan.Own(), out _);

        Assert.Equal(indexes, decoded.Indexed);
        Assert.Equal(replacement.Colors[0], decoded.Palette!.Colors[0]);
        Assert.Equal(replacement.Colors[1], decoded.Palette.Colors[1]);
        Assert.Equal(replacement.Colors[2], decoded.Palette.Colors[2]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(256)]
    public void JascPalWritesAndReadsItsVariableColorCount(int count)
    {
        var palette = new Palette
        {
            Colors = Enumerable.Range(0, count)
                .Select(i => new Rgba((byte)i, (byte)(255 - i), (byte)(i / 2), i == 0 ? (byte)0 : (byte)255))
                .ToArray(),
        };

        var imported = PaletteFileImport.Read("palette.pal", Encoding.UTF8.GetBytes(JascPalFile.Write(palette)));

        Assert.Equal(palette.Colors, imported.Colors);
        Assert.Contains($"\r\n{count}\r\n", JascPalFile.Write(palette));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActOrderingSurvivesPaletteFileRoundTrip(bool reversed)
    {
        var palette = new Palette
        {
            Colors = Enumerable.Range(0, 256)
                .Select(i => new Rgba((byte)i, (byte)(255 - i), (byte)(i * 3), i == 0 ? (byte)0 : (byte)255))
                .ToArray(),
        };

        var imported = PaletteFileImport.Read("palette.act", ActFile.Write(palette, reversed));

        Assert.Equal(palette.Colors, imported.Colors);
        Assert.Equal(reversed, imported.SourceActReversed);
    }

    [Fact]
    public void ReplacingPngPalettePreservesTableSizeAndPixels()
    {
        byte[] source = PngExport.EncodeIndexed([0, 1], 2, 1, [new(0, 0, 0, 0), C(20)]);
        var replacement = new Palette { Colors = [new(0, 0, 0, 0), C(80), C(120)] };

        byte[] replaced = PngExport.ReplacePalette(source, replacement);
        Assert.Equal(2, PngExport.DecodePaletteCount(replaced));
        var decoded = PngExport.DecodeToBgra(replaced);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 80, 80, 80, 255 }, decoded.Bgra);
    }

    [Fact]
    public void ReplacingPcxPalettePreservesTableSizeAndPixels()
    {
        byte[] source = PcxExport.EncodeIndexed([0, 1], 2, 1,
            new Palette { Colors = [new(0, 0, 0, 0), C(20), C(40)] });

        byte[] replaced = PcxExport.ReplacePalette(source,
            new Palette { Colors = [new(0, 0, 0, 0), C(80)] });
        Assert.Equal(256, PcxExport.DecodePaletteCount(replaced));

        var decoded = ImageImport.FromImage(replaced, ImportPlan.Own(), out _);
        Assert.Equal([0, 1], decoded.Indexed);
        Assert.Equal(C(80), decoded.Palette!.Colors[1]);
    }

    [Fact]
    public void IsIndexedSniffsTheIhdrColorType()
    {
        var indexed = PngExport.EncodeIndexed([0, 1], 2, 1, [new(0, 0, 0, 0), C(1)]);
        var direct = PngExport.Encode([1, 2, 3, 255, 4, 5, 6, 255], 2, 1);

        Assert.True(PngExport.IsIndexed(indexed));
        Assert.False(PngExport.IsIndexed(direct));
        Assert.False(PngExport.IsIndexed([1, 2, 3]));  // too short to even hold IHDR
    }

    // ---- v1 palette runs ---------------------------------------------------

    /// <summary>A(P1) B(P1) C(P1) D(P2): one run of three, then another carrier.</summary>
    private static (SffFile F, Sprite A, Sprite B, Sprite C, Sprite D, Palette P1, Palette P2) MakeV1Run()
    {
        var p1 = new Palette { Group = 1, Index = 1, Colors = [new(0, 0, 0, 0), C(10)] };
        var f = new SffFile { Version = SffVersions.V1, Palettes = [p1] };
        Sprite Make(int i) => new()
        {
            Group = 0, Index = i, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = p1,
        };
        var (a, b, c) = (Make(0), Make(1), Make(2));
        var p2 = new Palette { Group = 1, Index = 2, Colors = [new(0, 0, 0, 0), C(20)] };
        var d = Make(3);
        d.Palette = p2;
        f.Sprites.AddRange([a, b, c, d]);
        f.Palettes.Add(p2);
        return (f, a, b, c, d, p1, p2);
    }

    [Fact]
    public void MakingASpriteUniqueSplitsTheRunAtIt()
    {
        var (f, a, b, c, d, p1, p2) = MakeV1Run();

        var changed = f.MakePaletteUnique(b);

        Assert.Equal(2, changed.Count);                        // b and c moved
        Assert.NotSame(p1, b.Palette);
        Assert.Equal(p1.Colors, b.Palette!.Colors);            // same colors, own copy
        Assert.Same(b.Palette, c.Palette);                     // c follows b's new palette
        Assert.Same(p1, a.Palette);                            // sprites before are untouched
        Assert.Same(p2, d.Palette);                            // so is the next run
        Assert.Contains(b.Palette, f.Palettes);
    }

    [Fact]
    public void MakingAUniqueSpriteUniqueAgainDoesNothing()
    {
        var (f, a, b, c, _, p1, _) = MakeV1Run();
        f.MakePaletteUnique(b);
        var copy = b.Palette!;

        Assert.Empty(f.MakePaletteUnique(b));
        Assert.Same(copy, b.Palette);
        Assert.Same(p1, a.Palette);
        Assert.Same(copy, c.Palette);
    }

    [Fact]
    public void AV1PixelLinkCannotCarryAPalette()
    {
        var (f, a, b, _, _, _, _) = MakeV1Run();
        f.Share(b, a);

        Assert.False(f.PaletteCanBeMadeUnique(b));
        Assert.Empty(f.MakePaletteUnique(b));
        Assert.Empty(f.UsePreviousPalette(b));
    }

    [Fact]
    public void UsingThePreviousPaletteMovesTheRunToo()
    {
        var (f, _, b, c, d, p1, p2) = MakeV1Run();
        f.MakePaletteUnique(b);
        var copy = b.Palette!;

        var changed = f.UsePreviousPalette(b);

        Assert.Equal(2, changed.Count);        // b and its follower come back
        Assert.Same(p1, b.Palette);
        Assert.Same(p1, c.Palette);
        Assert.Same(p2, d.Palette);
        // The abandoned copy stays listed; deleting it is a separate decision.
        Assert.Contains(copy, f.Palettes);
    }

    [Fact]
    public void MakingARunCarrierUniqueDoesNothing()
    {
        // A carries the palette; B and C following it is the normal run state,
        // not sharing — so A is already unique and the operation is a no-op.
        var (f, a, b, c, d, p1, p2) = MakeV1Run();

        Assert.False(f.PaletteCanBeMadeUnique(a));
        Assert.Empty(f.MakePaletteUnique(a));

        Assert.Same(p1, a.Palette);
        Assert.Same(p1, b.Palette);
        Assert.Same(p1, c.Palette);
        Assert.Same(p2, d.Palette);
    }

    /// <summary>
    /// The whole model in one round trip: after a make-unique, the saved file
    /// says A carries palette 1, B carries its own copy, and C says "same as
    /// previous" — so a reload puts C back on B's object.
    /// </summary>
    [Fact]
    public void AUniqueRunSurvivesAV1SaveRoundTrip()
    {
        var (f, _, _, _, _, _, _) = MakeV1Run();
        f.MakePaletteUnique(f.Sprites[1]);

        var tmp = Path.Combine(Path.GetTempPath(), $"palrun-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            var reloaded = SffFile.Open(tmp);

            var (ra, rb, rc) = (reloaded.Sprites[0], reloaded.Sprites[1], reloaded.Sprites[2]);
            Assert.NotSame(ra.Palette, rb.Palette);
            Assert.Equal(ra.Palette!.Colors, rb.Palette!.Colors);
            Assert.Same(rb.Palette, rc.Palette);
        }
        finally { File.Delete(tmp); }
    }

    // ---- v1 links and the same-palette flag -------------------------------

    /// <summary>A gray sprite, a purple one, then a link to the gray one's pixels; the
    /// link's palette is set by the caller (as in sprites_org.sff's 6001 dust).</summary>
    private static (SffFile F, Sprite Link, Palette Gray, Palette Purple) MakeV1LinkAfterOtherPalette()
    {
        var gray = new Palette { Group = 1, Index = 1, Colors = [new(0, 0, 0, 0), C(10)] };
        var purple = new Palette { Group = 1, Index = 2, Colors = [new(0, 0, 0, 0), C(20)] };
        var f = new SffFile { Version = SffVersions.V1, Palettes = [gray, purple] };
        Sprite Make(int i, Palette p) => new()
        {
            Group = 6001, Index = i, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = p,
        };
        var (a, b, link) = (Make(0, gray), Make(6, purple), Make(7, purple));
        f.Sprites.AddRange([a, b, link]);
        f.Share(link, a);
        return (f, link, gray, purple);
    }

    /// <summary>Byte 18 (same palette) of the k-th v1 subheader.</summary>
    private static byte V1SamePaletteFlag(byte[] bytes, int k)
    {
        int off = BitConverter.ToInt32(bytes, 24);
        for (int i = 0; i < k; i++) off = BitConverter.ToInt32(bytes, off);
        return bytes[off + 18];
    }

    [Fact]
    public void AV1LinkFlaggedSamePaletteUsesThePreviousPalette()
    {
        var (f, link, _, purple) = MakeV1LinkAfterOtherPalette();
        Assert.Same(purple, link.Palette);

        var tmp = Path.Combine(Path.GetTempPath(), $"v1link-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            Assert.Equal(1, V1SamePaletteFlag(File.ReadAllBytes(tmp), 2));

            var r = SffFile.Open(tmp);
            Assert.Same(r.Sprites[0], r.Sprites[2].SharesWith);
            Assert.Same(r.Sprites[1].Palette, r.Sprites[2].Palette); // purple, not the target's gray
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void AV1LinkKeepsItsPreviousPaletteThroughAV2Save()
    {
        var (f, _, _, _) = MakeV1LinkAfterOtherPalette();
        var v1 = Path.Combine(Path.GetTempPath(), $"v1link-{Guid.NewGuid():N}.sff");
        var v2 = Path.Combine(Path.GetTempPath(), $"v2link-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(v1, SffVersions.V1);
            SffFile.Open(v1).Save(v2, SffVersions.V2);

            var r = SffFile.Open(v2);
            Assert.Same(r.Sprites[0], r.Sprites[2].SharesWith);
            Assert.Equal(r.Sprites[1].Palette!.Colors[1], r.Sprites[2].Palette!.Colors[1]);
            Assert.NotEqual(r.Sprites[0].Palette!.Colors[1], r.Sprites[2].Palette!.Colors[1]);
        }
        finally { File.Delete(v1); File.Delete(v2); }
    }

    [Fact]
    public void AV1LinkMatchingItsTargetKeepsTheUnflaggedBytes()
    {
        var (f, link, gray, _) = MakeV1LinkAfterOtherPalette();
        link.Palette = gray;

        var tmp = Path.Combine(Path.GetTempPath(), $"v1link-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            Assert.Equal(0, V1SamePaletteFlag(File.ReadAllBytes(tmp), 2));
            var r = SffFile.Open(tmp);
            Assert.Same(r.Sprites[0].Palette, r.Sprites[2].Palette);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SaveLikeSprMaker097KeepsItsLinkFlag()
    {
        // The SprMaker 0.97 writer must not pick up the plain-save link rule.
        var (f, _, _, _) = MakeV1LinkAfterOtherPalette();
        var tmp = Path.Combine(Path.GetTempPath(), $"v1sm-{Guid.NewGuid():N}.sff");
        try
        {
            SprMakerSave.Save(f, tmp, new SprMakerOptions());
            Assert.Equal(0, V1SamePaletteFlag(File.ReadAllBytes(tmp), 2));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void AV1LinkWithAThirdPaletteIsListedAsLost()
    {
        var (f, link, _, _) = MakeV1LinkAfterOtherPalette();
        Assert.Empty(SffWriter.V1LinksLosingPalette(f));

        var green = new Palette { Group = 1, Index = 3, Colors = [new(0, 0, 0, 0), C(30)] };
        f.Palettes.Add(green);
        link.Palette = green;
        Assert.Equal(new[] { "6001,7" }, SffWriter.V1LinksLosingPalette(f));
    }

    // ---- v2 ---------------------------------------------------------------

    [Fact]
    public void MakingUniqueInV2RepointsOnlyThatSprite()
    {
        var p = new Palette { Group = 1, Index = 1, Colors = [new(0, 0, 0, 0), C(1)] };
        var f = new SffFile { Version = SffVersions.V2, Palettes = [p] };
        var s1 = new Sprite { Group = 0, Index = 0, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = p };
        var s2 = new Sprite { Group = 0, Index = 1, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = p };
        f.Sprites.AddRange([s1, s2]);

        f.MakePaletteUnique(s2);

        Assert.Same(p, s1.Palette);
        Assert.NotSame(p, s2.Palette);
        Assert.Equal(p.Colors, s2.Palette!.Colors);
        Assert.Equal(2, f.Palettes.Count);
    }

    [Fact]
    public void UsingThePreviousPaletteInV2RepointsOnlyThatSprite()
    {
        var p1 = new Palette { Group = 1, Index = 1, Colors = [new(0, 0, 0, 0), C(1)] };
        var p2 = new Palette { Group = 1, Index = 2, Colors = [new(0, 0, 0, 0), C(2)] };
        var f = new SffFile { Version = SffVersions.V2, Palettes = [p1, p2] };
        var s1 = new Sprite { Group = 0, Index = 0, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = p1 };
        var s2 = new Sprite { Group = 0, Index = 1, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = p2 };
        f.Sprites.AddRange([s1, s2]);

        f.UsePreviousPalette(s2);

        Assert.Same(p1, s2.Palette);
        Assert.Same(p1, s1.Palette);
        Assert.Equal(2, f.Palettes.Count);   // no clone, no list change
    }

    // ---- identical-palette reuse -------------------------------------------

    [Fact]
    public void FindIdenticalPaletteMatchesColorsInOrder()
    {
        var p1 = new Palette { Colors = [new(0, 0, 0, 0), C(1), C(2)] };
        var f = new SffFile { Palettes = [p1] };

        Assert.Same(p1, f.FindIdenticalPalette(new Palette { Colors = [new(0, 0, 0, 0), C(1), C(2)] }));
        Assert.Null(f.FindIdenticalPalette(new Palette { Colors = [new(0, 0, 0, 0), C(2), C(1)] }));
        Assert.Null(f.FindIdenticalPalette(new Palette { Colors = [new(0, 0, 0, 0), C(1)] }));
    }

    [Fact]
    public void LinkedPalettesStayLinkedThroughASave()
    {
        var a = new Palette { Group = 1, Index = 1, Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var b = new Palette { Group = 1, Index = 2, LinksTo = a };
        var f = new SffFile { Palettes = [a, b] };
        f.Sprites.Add(new Sprite { Width = 2, Height = 2, Palette = b, Indexed = new byte[4], PixelsDirty = true });

        b.Colors[3] = new Rgba(9, 9, 9, 255);
        Assert.Equal(9, a.Colors[3].R); // one set of colors

        var tmp = Path.Combine(Path.GetTempPath(), $"pallink-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var bytes = File.ReadAllBytes(tmp);
            int palTable = BitConverter.ToInt32(bytes, 44);
            Assert.Equal(0, BitConverter.ToInt32(bytes, palTable + 16 + 12)); // second node: no data
            Assert.Equal(0, BitConverter.ToUInt16(bytes, palTable + 16 + 6));  // links to palette 0

            var r = SffFile.Open(tmp);
            Assert.Same(r.Palettes[0], r.Palettes[1].LinksTo);
            r.Palettes[1].Colors = new Rgba[32];
            Assert.Same(r.Palettes[0].Colors, r.Palettes[1].Colors); // replacing goes to the target too
        }
        finally { File.Delete(tmp); }
    }

    private static Rgba[] Ramp(byte g) => Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, g, 0, 255)).ToArray();

    /// <summary>Saves and reads back; per node, the link index read from disk (-1 when it has data).</summary>
    private static (SffFile File, int[] Links) SaveAndReadLinks(SffFile f)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pallink-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var bytes = File.ReadAllBytes(tmp);
            var r = SffFile.Open(tmp);
            int palTable = BitConverter.ToInt32(bytes, 44);
            var links = new int[r.Palettes.Count];
            for (int i = 0; i < links.Length; i++)
            {
                int n = palTable + i * 16;
                links[i] = BitConverter.ToInt32(bytes, n + 12) == 0 ? BitConverter.ToUInt16(bytes, n + 6) : -1;
            }
            return (r, links);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void LinkedPaletteBeforeItsTargetStoresTheColors()
    {
        var b = new Palette { Group = 1, Index = 2, Colors = Ramp(7) };
        var a = new Palette { Group = 1, Index = 1, LinksTo = b };
        var f = new SffFile { Palettes = [a, b] };
        f.Sprites.Add(new Sprite { Width = 2, Height = 2, Palette = a, Indexed = new byte[4], PixelsDirty = true });

        var (r, links) = SaveAndReadLinks(f);
        Assert.Equal(new[] { -1, 0 }, links); // node 0 has data, node 1 links back to it
        Assert.Equal(Ramp(7), r.Palettes[0].Colors);
        Assert.Same(r.Palettes[0], r.Palettes[1].LinksTo);
        Assert.Same(r.Palettes[0], r.Sprites[0].Palette);
    }

    [Fact]
    public void PaletteChainsAndUnlinkedPalettesLinkBackward()
    {
        // c -> b -> a with a last, plus an unlinked palette and a backward link left as is.
        var a = new Palette { Group = 1, Index = 4, Colors = Ramp(1) };
        var b = new Palette { Group = 1, Index = 2, LinksTo = a };
        var c = new Palette { Group = 1, Index = 1, LinksTo = b };
        var solo = new Palette { Group = 1, Index = 3, Colors = Ramp(2) };
        var back = new Palette { Group = 1, Index = 5, LinksTo = solo };
        var f = new SffFile { Palettes = [c, b, solo, a, back] };

        var (r, links) = SaveAndReadLinks(f);
        Assert.Equal(new[] { -1, 0, -1, 0, 2 }, links);
        Assert.All(links.Select((t, i) => (t, i)), x => Assert.True(x.t < x.i));
        Assert.Equal(Ramp(1), r.Palettes[3].Colors);
        Assert.Equal(Ramp(2), r.Palettes[4].Colors);
        Assert.Equal(new[] { (1, 1), (1, 2), (1, 3), (1, 4), (1, 5) }, r.Palettes.Select(p => (p.Group, p.Index)).ToArray());
    }

    [Fact]
    public void ForwardPaletteLinksAreFlaggedOnLoadAndTurnedOnSave()
    {
        // A file with node 0 linking to node 1, as older saves wrote after a reorder.
        var b = new Palette { Group = 1, Index = 2, Colors = Ramp(7) };
        var a = new Palette { Group = 1, Index = 1, LinksTo = b };
        var tmp = Path.Combine(Path.GetTempPath(), $"pallink-{Guid.NewGuid():N}.sff");
        try
        {
            new SffFile { Palettes = [b, a] }.Save(tmp);
            var bytes = File.ReadAllBytes(tmp);
            int palTable = BitConverter.ToInt32(bytes, 44);
            for (int k = 0; k < 16; k++) (bytes[palTable + k], bytes[palTable + 16 + k]) = (bytes[palTable + 16 + k], bytes[palTable + k]);
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, palTable + 6); // the link node, now first, targets 1
            File.WriteAllBytes(tmp, bytes);

            var r = SffFile.Open(tmp);
            Assert.Same(r.Palettes[1], r.Palettes[0].LinksTo);
            Assert.Equal(new[] { r.Palettes[0] }, r.ForwardPaletteLinksOnLoad);
            Assert.Equal(new[] { r.Palettes[0] }, SffWriter.ForwardPaletteLinksTurnedOnSave(r));
            var c = r.Clone();
            Assert.Same(c.Palettes[0], Assert.Single(c.ForwardPaletteLinksOnLoad));

            (r.Palettes[0], r.Palettes[1]) = (r.Palettes[1], r.Palettes[0]); // moved after its target: nothing to turn
            Assert.Empty(SffWriter.ForwardPaletteLinksTurnedOnSave(r));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void BackwardPaletteLinksAreNotFlagged()
    {
        var a = new Palette { Group = 1, Index = 1, Colors = Ramp(1) };
        var f = new SffFile { Palettes = [a, new Palette { Group = 1, Index = 2, LinksTo = a }] };
        var tmp = Path.Combine(Path.GetTempPath(), $"pallink-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            Assert.Empty(SffFile.Open(tmp).ForwardPaletteLinksOnLoad);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UnlinkedPalettesAreWrittenInFull()
    {
        var f = new SffFile { Palettes = [new Palette { Group = 1, Index = 1, Colors = Ramp(1) }, new Palette { Group = 1, Index = 2, Colors = Ramp(2) }] };
        var (r, links) = SaveAndReadLinks(f);
        Assert.Equal(new[] { -1, -1 }, links);
        Assert.Equal(Ramp(2), r.Palettes[1].Colors);
    }

    [Fact]
    public void PalettePngKeepsAlphaAndOrder()
    {
        var colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, (byte)(255 - i), (byte)(i / 2), (byte)(i % 7 == 0 ? 0 : 255 - i))).ToArray();
        var tmp = Path.Combine(Path.GetTempPath(), $"palpng-{Guid.NewGuid():N}.png");
        try
        {
            PalettePngFile.Save(new Palette { Colors = colors }, tmp, 3);
            Assert.Equal(colors, PaletteFileImport.Read(tmp).Colors); // RGBA and index order survive
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void RgbaPaletteKeepsExactColorsAndOrder(int count)
    {
        var colors = Enumerable.Range(0, count)
            .Select(i => new Rgba((byte)i, (byte)(255 - i), (byte)(i * 17), (byte)(i * 31))).ToArray();
        var palette = new Palette { Colors = colors };

        var imported = PaletteFileImport.Read("palette.rgba", Encoding.UTF8.GetBytes(PaletteRgbaFile.Write(palette)));

        Assert.Equal(colors, imported.Colors);
    }

    [Fact]
    public void RgbaPaletteRejectsMalformedFiles()
    {
        const string comment = "# RGBA palette; colors are listed by index as R G B A values from 0 to 255.";
        var malformed = new[]
        {
            string.Join(Environment.NewLine, comment, "IKEMEN-RGBA", "1", "2", "1 2 3 4"),
            string.Join(Environment.NewLine, comment, "IKEMEN-RGBA", "1", "1", "1 2 3"),
            string.Join(Environment.NewLine, comment, "IKEMEN-RGBA", "1", "1", "1 2 3 256"),
            string.Join(Environment.NewLine, comment, "IKEMEN-RGBA", "2", "1", "1 2 3 4"),
            string.Join(Environment.NewLine, comment, "", "IKEMEN-RGBA", "1", "1", "1 2 3 4"),
        };

        foreach (string text in malformed)
            Assert.Throws<InvalidDataException>(() => PaletteRgbaFile.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void RgbaPaletteRequiresItsCommentHeader()
    {
        var text = PaletteRgbaFile.Write(new Palette { Colors = [new Rgba(1, 2, 3, 4)] })
            .Replace("# RGBA palette; colors are listed by index as R G B A values from 0 to 255.\n", "", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => PaletteRgbaFile.Read(Encoding.UTF8.GetBytes(text)));
    }
}
