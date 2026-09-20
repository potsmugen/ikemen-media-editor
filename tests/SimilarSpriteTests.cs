using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Find Similar Sprites compares normalized visible pixels.
/// Transparent positions do not count, and lower percentages accept more visible
/// pixel differences. Indexed and true-color sprites remain separate kinds.
/// </summary>
public class SimilarSpriteTests
{
    private static Palette Palette(byte fill = 0) =>
        new() { Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, fill, 0, 255)).ToArray() };

    /// <summary>A 10x10 indexed sprite: the first `matches` pixels share the reference's fill.</summary>
    private static Sprite Indexed(int group, Palette pal, byte[] pixels, int width = 10, int height = 10) =>
        new() { Group = group, Index = 0, Width = width, Height = height, Palette = pal, Indexed = pixels, PixelsDirty = true };

    private static Sprite Reference(Palette pal, byte[]? pixels = null) =>
        Indexed(1, pal, pixels ?? Enumerable.Repeat((byte)1, 100).ToArray());

    [Fact]
    public void ExactCopiesMatchAtOneHundredPercent()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        f.Sprites.Add(Indexed(2, pal, Enumerable.Repeat((byte)1, 100).ToArray()));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 100);

        Assert.Single(matches);
        Assert.Equal(100, matches[0].Percent);
        Assert.Equal(2, matches[0].Sprite.Group);
    }

    [Fact]
    public void SparseUnrelatedDrawingsDoNotMatchAtTheSimilarLevel()
    {
        var pal = Palette();
        var cross = new byte[]
        {
            0, 1, 0,
            1, 1, 1,
            0, 1, 0,
        };
        var diagonals = new byte[]
        {
            1, 0, 1,
            0, 1, 0,
            1, 0, 1,
        };
        var f = new SffFile();
        f.Sprites.Add(Indexed(1, pal, cross, width: 3, height: 3));
        f.Sprites.Add(Indexed(2, pal, diagonals, width: 3, height: 3));

        Assert.Empty(f.FindSimilarSprites(f.Sprites[0], percent: 80));
    }

    [Fact]
    public void ThePercentageCountsMatchingPixels()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));                                     // all 1s
        f.Sprites.Add(Indexed(2, pal, Enumerable.Repeat((byte)1, 90).Concat(Enumerable.Repeat((byte)2, 10)).ToArray()));
        f.Sprites.Add(Indexed(3, pal, Enumerable.Repeat((byte)1, 50).Concat(Enumerable.Repeat((byte)2, 50)).ToArray()));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);

        Assert.Equal(2, matches.Count);
        // Best first.
        Assert.Equal(90, matches[0].Percent);
        Assert.Equal(2, matches[0].Sprite.Group);
        Assert.Equal(50, matches[1].Percent);
        Assert.Equal(3, matches[1].Sprite.Group);
    }

    [Fact]
    public void SpritesBelowTheThresholdAreLeftOut()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        f.Sprites.Add(Indexed(2, pal, Enumerable.Repeat((byte)1, 95).Concat(Enumerable.Repeat((byte)2, 5)).ToArray()));
        f.Sprites.Add(Indexed(3, pal, Enumerable.Repeat((byte)1, 50).Concat(Enumerable.Repeat((byte)2, 50)).ToArray()));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 90);

        Assert.Single(matches);
        Assert.Equal(2, matches[0].Sprite.Group);
    }

    [Fact]
    public void ADifferentSizeStillMatchesWhenOneDrawingContainsTheOther()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal)); // a 10x10 block, already tight
        // Same 10x10 drawing with a border of mask color around it: cropping makes them equal.
        var padded = new byte[14 * 14];
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                padded[(y + 2) * 14 + (x + 2)] = 1;
        f.Sprites.Add(Indexed(2, pal, padded, width: 14, height: 14));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 100);

        Assert.Single(matches);
        Assert.Equal(100, matches[0].Percent);
    }

    [Fact]
    public void AStrayPixelCostsOnlyThatPixel()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal)); // a 10x10 block
        var stray = Enumerable.Repeat((byte)0, 110).ToList();
        for (int i = 0; i < 100; i++) stray[i] = 1;
        stray[100] = 3; // one extra pixel below the block, so the tight box is 10x11
        f.Sprites.Add(Indexed(2, pal, stray.ToArray(), width: 10, height: 11));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);

        Assert.Single(matches);
        // The added visible pixel is one difference among 101 visible positions.
        Assert.Equal(99, matches[0].Percent);
        Assert.Equal(2, matches[0].Sprite.Group);
    }

    [Fact]
    public void AnEmptyBoxMatchesNothing()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        // Entirely mask color: no tight box at all.
        f.Sprites.Add(Indexed(2, pal, Enumerable.Repeat((byte)0, 100).ToArray()));
        f.Sprites.Add(Indexed(3, pal, Enumerable.Repeat((byte)1, 100).ToArray()));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);

        Assert.Single(matches);
        Assert.Equal(3, matches[0].Sprite.Group); // the blank one was skipped, not a 0% wildcard
    }

    [Fact]
    public void TheSameDrawingAtADifferentPlaceStillMatches()
    {
        // The axis and canvas position are deliberately not part of the comparison, so a
        // drawing moved down its canvas normalizes to the same box.
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        var moved = new byte[10 * 16];
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                moved[(y + 6) * 10 + x] = 1; // the same block, six rows lower
        f.Sprites.Add(Indexed(2, pal, moved, width: 10, height: 16));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 100);

        Assert.Single(matches);
        Assert.Equal(100, matches[0].Percent);
    }

    [Fact]
    public void ADifferentSizeScoresBelowOneHundredPercent()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal)); // 10x10
        // 10x20 whose lower half is more picture, not mask: cropping can't hide it, so the
        // union is 200 pixels of which 100 agree.
        var taller = new byte[10 * 20];
        for (int i = 0; i < 200; i++) taller[i] = (byte)(i < 100 ? 1 : 2);
        f.Sprites.Add(Indexed(2, pal, taller, width: 10, height: 20));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);

        Assert.Single(matches);
        Assert.Equal(50, matches[0].Percent);
        Assert.Empty(f.FindSimilarSprites(f.Sprites[0], percent: 90));
    }

    [Fact]
    public void IndexedAndTrueColorNeverMatch()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        f.Sprites.Add(new Sprite
        {
            Group = 2, Width = 10, Height = 10, Palette = pal,
            Direct = Enumerable.Repeat(new Rgba(1, 1, 1, 255), 100).SelectMany(c => new[] { c.B, c.G, c.R, c.A }).ToArray(),
            PixelsDirty = true,
        });

        Assert.Empty(f.FindSimilarSprites(f.Sprites[0], percent: 0));
    }

    [Fact]
    public void TrueColorSpritesComparePixelByPixel()
    {
        static byte[] Pixels(int identical)
        {
            var list = new List<byte>();
            for (int i = 0; i < 100; i++)
            {
                var (b, g, r, a) = i < identical ? ((byte)7, (byte)8, (byte)9, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255);
                list.AddRange([b, g, r, a]);
            }
            return list.ToArray();
        }
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(new Sprite { Group = 1, Width = 10, Height = 10, Palette = pal, Direct = Pixels(100), PixelsDirty = true });
        f.Sprites.Add(new Sprite { Group = 2, Width = 10, Height = 10, Palette = pal, Direct = Pixels(80), PixelsDirty = true });

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);

        Assert.Single(matches);
        Assert.Equal(80, matches[0].Percent);
    }

    [Fact]
    public void RequiringTheSamePaletteDropsARecoloredSprite()
    {
        var pal = Palette();
        var redone = new Palette { Colors = pal.Colors.Select(c => c with { G = (byte)(c.G ^ 0xFF) }).ToArray() };
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        f.Sprites.Add(Indexed(2, redone, Enumerable.Repeat((byte)1, 100).ToArray()));

        Assert.Single(f.FindSimilarSprites(f.Sprites[0], percent: 0)); // pixels alone match
        Assert.Empty(f.FindSimilarSprites(f.Sprites[0], percent: 0, requireSamePalette: true));
    }

    [Fact]
    public void ALinkedPaletteCountsAsTheSameColors()
    {
        var pal = Palette();
        var link = new Palette { LinksTo = pal };
        var f = new SffFile { Palettes = [pal, link] };
        f.Sprites.Add(Reference(pal));
        f.Sprites.Add(Indexed(2, link, Enumerable.Repeat((byte)1, 100).ToArray()));

        Assert.Single(f.FindSimilarSprites(f.Sprites[0], percent: 0, requireSamePalette: true));
    }

    [Fact]
    public void LinkedAndBrokenSpritesAreLeftOut()
    {
        var pal = Palette();
        var f = new SffFile();
        var reference = Reference(pal);
        f.Sprites.Add(reference);
        var copy = Indexed(2, pal, Enumerable.Repeat((byte)1, 100).ToArray());
        f.Sprites.Add(copy);
        // Borrows the copy's pixels, so it would always match it.
        var borrower = Indexed(3, pal, []);
        f.Sprites.Add(borrower);
        f.Share(borrower, copy);
        f.Sprites.Add(new Sprite { Group = 4, Width = 10, Height = 10, Palette = pal, DecodeError = "bad data" });

        var matches = f.FindSimilarSprites(reference, percent: 0);

        Assert.Single(matches);
        Assert.Equal(2, matches[0].Sprite.Group);
    }

    [Fact]
    public void TheReferenceItselfIsNeverListed()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));

        Assert.Empty(f.FindSimilarSprites(f.Sprites[0], percent: 0));
    }

    [Fact]
    public void MatchesComeBackBestFirst()
    {
        var pal = Palette();
        var f = new SffFile();
        f.Sprites.Add(Reference(pal));
        f.Sprites.Add(Indexed(2, pal, Enumerable.Repeat((byte)1, 60).Concat(Enumerable.Repeat((byte)2, 40)).ToArray()));
        f.Sprites.Add(Indexed(3, pal, Enumerable.Repeat((byte)1, 95).Concat(Enumerable.Repeat((byte)2, 5)).ToArray()));

        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);

        Assert.Equal([95, 60], matches.Select(m => m.Percent));
    }

    [Fact]
    public void AnEmptyOrBrokenReferenceFindsNothing()
    {
        var pal = Palette();
        var f = new SffFile();
        var empty = Indexed(1, pal, []);
        f.Sprites.Add(empty);
        f.Sprites.Add(Indexed(2, pal, Enumerable.Repeat((byte)1, 100).ToArray()));
        var broken = new Sprite { Group = 3, Width = 10, Height = 10, Palette = pal, DecodeError = "bad data" };
        f.Sprites.Add(broken);
        f.Sprites.Add(Indexed(4, pal, Enumerable.Repeat((byte)1, 100).ToArray()));

        Assert.Empty(f.FindSimilarSprites(empty, percent: 0));
        Assert.Empty(f.FindSimilarSprites(broken, percent: 0));
    }
}
