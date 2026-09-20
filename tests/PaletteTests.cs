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
