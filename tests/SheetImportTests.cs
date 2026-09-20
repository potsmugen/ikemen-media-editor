using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

// xUnit parallelizes across collections, and both this class and
// SheetRealFileTests write the process-global SheetImport.MinimumSize, so they
// share one collection and never run at the same time.
[CollectionDefinition(Name, DisableParallelization = true)]
public class SheetImportCollection
{
    public const string Name = "SheetImport";
}

[Collection(SheetImportCollection.Name)]
public class SheetImportTests : IDisposable
{
    /// <summary>
    /// Detection reads a static floor, so each test starts from the default.
    /// </summary>
    public void Dispose() => SheetImport.MinimumSize = SheetImport.DefaultMinimumSize;

    /// <summary>
    /// Runs detection with the size floor off, for the tests about finding and
    /// cropping rather than about filtering: their sprites are a few pixels
    /// across, which the default floor would (correctly) throw away.
    /// </summary>
    private static SheetLayout DetectAll(Png.Image img)
    {
        SheetImport.MinimumSize = 0;
        return SheetImport.Detect(img);
    }
    /// <summary>
    /// A BGRA sheet built from an ASCII map: '.' is transparent, any other
    /// character is a color. Transparent by default so most tests exercise
    /// detection rather than the background guess.
    /// </summary>
    private static byte[] Sheet(string[] rows, out int width, out int height)
    {
        width = rows.Max(r => r.Length);
        height = rows.Length;
        var bgra = new byte[width * height * 4];
        // Each row is padded to the sheet's width rather than counted by hand,
        // so a ragged map fails on what it means, not on a character count.
        int sheetWidth = width;
        var padded = rows.Select(r => r.Length >= sheetWidth ? r : r.PadRight(sheetWidth, '.')).ToArray();
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(width, padded[y].Length);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (padded[y][x] == '.') continue; // stays 0,0,0,0
                byte v = (byte)('a' + (padded[y][x] - 'a') * 20);
                bgra[i + 2] = v; bgra[i + 1] = v; bgra[i] = v; bgra[i + 3] = 255;
            }
        }
        return bgra;
    }

    // Split on both, so the map reads the same whatever line endings the file has.
    private static string[] Rows(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static Png.Image Image(byte[] bgra, int width, int height) =>
        new(width, height, null, bgra, null);

    [Fact]
    public void FindsTwoBlobsAndCropsThemTight()
    {
        var bgra = Sheet(Rows("""
            aa....
            aa....
            ...bbb
            ...bbb
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new SheetCell(0, 0, 2, 2), layout.Cells[0]);
        Assert.Equal(new SheetCell(3, 2, 3, 2), layout.Cells[1]);
    }

    [Fact]
    public void CellsAreOrderedTopToBottomThenLeftToRight()
    {
        // With no vertical overlap, the box with the higher top edge stays first.
        var bgra = Sheet(Rows("""
            ......aa
            ......aa
            bb......
            bb......
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new SheetCell(6, 0, 2, 2), layout.Cells[0]);
        Assert.Equal(new SheetCell(0, 2, 2, 2), layout.Cells[1]);
    }

    [Fact]
    public void CellsWithSubstantialVerticalOverlapShareALeftmostAnchoredBand()
    {
        var bgra = Sheet(Rows("""
            ....bb..
            ....bb..
            aa..bb..
            aa..bb..
            aa......
            aa......
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new SheetCell(0, 2, 2, 4), layout.Cells[0]);
        Assert.Equal(new SheetCell(4, 0, 2, 4), layout.Cells[1]);
    }

    [Fact]
    public void CellsOnTheSameRowGoLeftToRight()
    {
        var bgra = Sheet(Rows("""
            aa..bb..cc
            aa..bb..cc
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(3, layout.Count);
        Assert.Equal(0, layout.Cells[0].X);
        Assert.Equal(4, layout.Cells[1].X);
        Assert.Equal(8, layout.Cells[2].X);
    }

    [Fact]
    public void StaggeredSpritesStayApart()
    {
        // Overlapping in both axes without touching. Splitting on empty rows
        // and columns would merge these into one box; connected components
        // must not.
        var bgra = Sheet(Rows("""
            aaaa......
            aaaa......
            aaaa..bbbb
            ......bbbb
            ......bbbb
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new SheetCell(0, 0, 4, 3), layout.Cells[0]);
        Assert.Equal(new SheetCell(6, 2, 4, 3), layout.Cells[1]);
    }

    [Fact]
    public void SpritesTouchingOnAnEdgeStayMerged()
    {
        // Deliberate: separating these needs an erode pass, which shaves a real
        // pixel off every sprite. A wrong-but-visible box beats a silent crop.
        var bgra = Sheet(Rows("""
            ........
            .aabb...
            .aabb...
            ........
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Single(layout.Cells);
        Assert.Equal(new SheetCell(1, 1, 4, 2), layout.Cells[0]);
    }

    [Fact]
    public void DiagonallyTouchingSpritesStayApart()
    {
        // 4-connected on purpose, so a corner touch does not join them.
        var bgra = Sheet(Rows("""
            aa..
            aa..
            ..bb
            ..bb
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(2, layout.Count);
    }

    [Fact]
    public void AHoleInsideASpriteDoesNotSplitIt()
    {
        var bgra = Sheet(Rows("""
            aaaa
            a..a
            a..a
            aaaa
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Single(layout.Cells);
        Assert.Equal(new SheetCell(0, 0, 4, 4), layout.Cells[0]);
    }

    [Fact]
    public void AnEmptySheetFindsNothing()
    {
        var bgra = Sheet(Rows("""
            ....
            ....
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Equal(0, layout.Count);
        Assert.Equal("0 sprites found", layout.ToString());
    }

    [Fact]
    public void AnOpaqueSheetTreatsItsMostCommonColorAsBackground()
    {
        // No transparency anywhere, so the background is a guess and must say
        // so rather than silently eating a sprite.
        var bgra = Sheet(Rows("""
            ........
            ..aa....
            ..aa....
            ........
            """), out int w, out int h);
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255; // flatten to opaque

        var layout = DetectAll(Image(bgra, w, h));

        Assert.Single(layout.Cells);
        Assert.Equal(new SheetCell(2, 1, 2, 2), layout.Cells[0]);
        Assert.True(layout.AssumedBackground);
        Assert.Equal(new Rgba(0, 0, 0, 255), layout.Background);
        Assert.Contains("RGB(0, 0, 0)", layout.ToString());
    }

    [Fact]
    public void ATransparentSheetIsNotAGuess()
    {
        var bgra = Sheet(Rows("""
            ..aa
            ..aa
            """), out int w, out int h);

        var layout = DetectAll(Image(bgra, w, h));

        Assert.False(layout.AssumedBackground);
        Assert.Null(layout.Background);
        Assert.Equal("1 sprite found", layout.ToString());
    }

    [Fact]
    public void AnIndexedSheetTreatsIndexZeroAsBackground()
    {
        var indices = new byte[] { 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1 };
        var plte = new[] { new Rgba(0, 0, 0, 0), new Rgba(255, 0, 0, 255) };

        var layout = DetectAll(new Png.Image(4, 3, indices, null, plte));

        Assert.Single(layout.Cells);
        Assert.Equal(new SheetCell(2, 1, 2, 2), layout.Cells[0]);
        Assert.False(layout.AssumedBackground);
    }

    [Fact]
    public void EveryCellBecomesASpriteAtItsCroppedSize()
    {
        var bgra = Sheet(Rows("""
            aa..bbbb
            aa..bbbb
            """), out int w, out int h);
        var img = Image(bgra, w, h);

        var sprites = SheetImport.ToSprites(img, DetectAll(img), ImportPlan.TrueColor32(), out var reports);

        Assert.Equal(2, sprites.Count);
        Assert.Equal((2, 2), (sprites[0].Width, sprites[0].Height));
        Assert.Equal((4, 2), (sprites[1].Width, sprites[1].Height));
        Assert.Equal(2, reports.Count);
        Assert.All(sprites, s => Assert.NotNull(s.Direct));
    }

    [Fact]
    public void CellsShareOnePaletteBuiltFromTheSheet()
    {
        var bgra = Sheet(Rows("""
            aaa..ccc
            aaa..ccc
            """), out int w, out int h);
        var img = Image(bgra, w, h);

        var sprites = SheetImport.ToSprites(img, DetectAll(img), ImportPlan.Own(), out _);

        Assert.Equal(2, sprites.Count);
        Assert.Same(sprites[0].Palette, sprites[1].Palette); // one instance, not two equal ones
        Assert.Equal(256, sprites[0].Palette!.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 255), sprites[0].Palette!.Colors[3]);
        Assert.All(sprites, s => Assert.All(s.Indexed!, i => Assert.NotEqual(Sprite.TransparentIndex, i)));
    }

    [Fact]
    public void AnIndexedSheetKeepsItsIndexesAndSharesOnePalette()
    {
        // Two 1-based cells, separated by an empty row, so the cells cannot
        // touch and the indexes stay exactly as authored.
        var indices = new byte[]
        {
            0, 0, 1, 1, 0,
            0, 0, 1, 1, 0,
            0, 0, 0, 0, 0,
            0, 2, 2, 2, 0,
            0, 2, 2, 2, 0,
        };
        var plte = new[] { new Rgba(0, 0, 0, 0), new Rgba(255, 0, 0, 255), new Rgba(0, 255, 0, 255) };
        var img = new Png.Image(5, 5, indices, null, plte);

        var sprites = SheetImport.ToSprites(img, DetectAll(img), ImportPlan.Own(), out _);

        Assert.Equal(2, sprites.Count);
        Assert.Same(sprites[0].Palette, sprites[1].Palette);
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, sprites[0].Indexed);
        Assert.Equal(new byte[] { 2, 2, 2, 2, 2, 2 }, sprites[1].Indexed);
    }

    [Fact]
    public void ASpritesCroppedPixelsAreTheOnesFound()
    {
        // Proves the crop is a real cut, not a resample: a single opaque pixel
        // becomes a 1x1 sprite, and nothing outside its box leaks in.
        var bgra = new byte[5 * 3 * 4];
        void Set(int x, int y)
        {
            int i = (y * 5 + x) * 4;
            bgra[i + 2] = 255; bgra[i + 3] = 255;
        }
        Set(1, 1);
        Set(4, 0);
        var img = Image(bgra, 5, 3);

        var layout = DetectAll(img);
        var sprites = SheetImport.ToSprites(img, layout, ImportPlan.TrueColor32(), out _);

        Assert.Equal(2, sprites.Count);
        Assert.All(sprites, s => Assert.Equal(1, s.Width * s.Height));
        Assert.All(sprites, s => Assert.Equal(255, s.Direct![3])); // opaque, not transparent
    }

    [Fact]
    public void ASheetNeedingMoreColorsThanAPaletteIsRefusedWithoutAnExplicitChoice()
    {
        const int cells = 300, gap = 2;
        int w = cells * gap, h = gap;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < cells; i++)
        {
            int at = (i * gap) * 4;
            bgra[at + 2] = (byte)(i & 0xFF);
            bgra[at + 1] = (byte)(i >> 8);
            bgra[at + 3] = 255;
        }
        var img = Image(bgra, w, h);
        var layout = DetectAll(img);

        Assert.Equal(cells, layout.Count);
        Assert.Throws<NotSupportedException>(
            () => SheetImport.ToSprites(img, layout, ImportPlan.Own(), out _));
    }

    [Fact]
    public void ASheetCanBeReducedToOneSharedEightBitPalette()
    {
        const int cells = 300, gap = 2;
        int w = cells * gap, h = gap;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < cells; i++)
        {
            int at = (i * gap) * 4;
            bgra[at + 2] = (byte)(i & 0xFF);
            bgra[at + 1] = (byte)(i >> 8);
            bgra[at + 3] = 255;
        }
        var img = Image(bgra, w, h);
        var layout = DetectAll(img);
        var colors = SheetImport.SharedColors(PngExport.Encode(bgra, w, h), layout);
        var palette = colors.Reduced();

        Assert.Equal(cells, colors.ColorCount);
        Assert.Equal(256, palette.Colors.Length);
        var sprites = SheetImport.ToSprites(img, layout, ImportPlan.Own(), palette, out _);

        Assert.Equal(cells, sprites.Count);
        Assert.All(sprites, sprite => Assert.Same(palette, sprite.Palette));
    }

    [Fact]
    public void SpritesSmallerThanTheFloorAreDropped()
    {
        // A 2x2 and a 20x20 beside each other, in a sheet with a transparent border
        // so the background is not guessed: with the default 16px floor the
        // small one is speckle as far as detection is concerned.
        var bgra = Sheet(Rows("""
            ..........................
            ......aa....................
            ......aa....................
            ..........................
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ......bbbbbbbbbbbbbbbbbbbb..
            ..........................
            """), out int w, out int h);
        var img = Image(bgra, w, h);

        SheetImport.MinimumSize = 16;
        Assert.Single(SheetImport.Detect(img).Cells);

        SheetImport.MinimumSize = 0;
        Assert.Equal(2, SheetImport.Detect(img).Count);
    }

    [Fact]
    public void TheFloorIsMeasuredOnTheLongerSide()
    {
        // Tall and thin: 2 wide by 20 tall, standing in a larger transparent
        // sheet. A floor read on the width would throw away a real sprite, so
        // the longer side is what counts.
        const int w = 30, h = 30;
        var bgra = new byte[w * h * 4];
        for (int y = 5; y < 25; y++)
            for (int x = 10; x < 12; x++)
                bgra[(y * w + x) * 4 + 3] = 255;

        SheetImport.MinimumSize = 16;
        var layout = SheetImport.Detect(Image(bgra, w, h));

        Assert.Single(layout.Cells);
        Assert.Equal(new SheetCell(10, 5, 2, 20), layout.Cells[0]);
    }

    [Fact]
    public void ASheetTooWideForItsBufferIsRefused()
    {
        var img = new Png.Image(4, 4, new byte[4], null, new[] { new Rgba(0, 0, 0, 0), new Rgba(1, 1, 1, 255) });

        Assert.Throws<InvalidDataException>(() => SheetImport.Detect(img));
    }

    [Fact]
    public void DetectsFromAPngFileEndToEnd()
    {
        var bgra = Sheet(Rows("""
            aa..bb
            aa..bb
            """), out int w, out int h);

        // Through the file entry point, not a decoded image, so this also
                // covers decoding; the floor comes off since the sprites are tiny.
                SheetImport.MinimumSize = 0;
                var layout = SheetImport.Detect(PngExport.Encode(bgra, w, h));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new SheetCell(0, 0, 2, 2), layout.Cells[0]);
        Assert.Equal(new SheetCell(4, 0, 2, 2), layout.Cells[1]);
    }

    [Fact]
    public void LargeSheetDoesNotRunTheStackOut()
    {
        // A recursive fill dies here; the explicit stack does not. One blob
        // inside a generous transparent border, so the sheet is plainly
        // transparent (well past MinimumTransparentPercent) and the result
        // stays cheap to assert.
        const int side = 700, margin = side / 4;
        var bgra = new byte[side * side * 4];
        for (int y = margin; y < side - margin; y++)
            for (int x = margin; x < side - margin; x++)
                bgra[(y * side + x) * 4 + 3] = 255;

        var layout = SheetImport.Detect(Image(bgra, side, side));

        Assert.Single(layout.Cells);
        Assert.Equal(new SheetCell(margin, margin, side - margin * 2, side - margin * 2), layout.Cells[0]);
    }

    [Fact]
    public void OneStrayTransparentPixelDoesNotFuseAKeyedSheet()
    {
        // A keyed sheet whose corner carries a single semi-transparent pixel.
        // Reading it by alpha alone would take the key color -- which surrounds
        // and separates every sprite -- for content, and join the lot into one
        // component the size of the sheet.
        const int w = 80, h = 40;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i + 1] = 255; bgra[i + 3] = 255; } // green key
        foreach (var (x, y) in new[] { (4, 4), (44, 16) })
            for (int yy = y; yy < y + 20; yy++)
                for (int xx = x; xx < x + 20; xx++)
                {
                    int at = (yy * w + xx) * 4;
                    bgra[at + 2] = 255; bgra[at + 3] = 255; // blue
                }
        bgra[3] = 0; // the one stray transparent pixel, top-right corner

        var layout = SheetImport.Detect(Image(bgra, w, h));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new SheetCell(4, 4, 20, 20), layout.Cells[0]);
        Assert.Equal(new SheetCell(44, 16, 20, 20), layout.Cells[1]);
        Assert.True(layout.AssumedBackground);
        Assert.Equal(new Rgba(0, 255, 0, 255), layout.Background);
    }

    [Fact]
    public void ARealTransparentSheetIsStillReadByItsTransparency()
    {
        // The counterpart to the stray-pixel case: when most of the sheet is
        // transparent, that transparency is the background and no color is
        // guessed, however uniform the opaque pixels are.
        const int w = 40, h = 40;
        var bgra = new byte[w * h * 4];
        for (int y = 10; y < 30; y++)
            for (int x = 10; x < 30; x++)
                bgra[(y * w + x) * 4 + 3] = 255;

        var layout = SheetImport.Detect(Image(bgra, w, h));

        Assert.Single(layout.Cells);
        Assert.False(layout.AssumedBackground);
        Assert.Null(layout.Background);
    }

    [Fact]
    public void TheGuessedBackgroundIsClearedFromInsideASprite()
    {
        // A key color showing through a sprite's hole is background, not
        // picture: without clearing it the sprite is imported with its own
        // key color baked in.
        const int w = 30, h = 30;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i + 1] = 255; bgra[i + 3] = 255; } // green key
        for (int y = 5; y < 25; y++)
            for (int x = 5; x < 25; x++)
            {
                int at = (y * w + x) * 4;
                bgra[at + 2] = 255; bgra[at + 3] = 255; // blue square
            }
        bgra[(15 * w + 15) * 4 + 2] = 0; // a green hole in the middle of it

        var img = Image(bgra, w, h);
        var sprites = SheetImport.ToSprites(img, SheetImport.Detect(img), ImportPlan.TrueColor32(), out _);

        var sprite = Assert.Single(sprites);
        int center = (10 * sprite.Width + 10) * 4;
        Assert.Equal(0, sprite.Direct![center + 3]); // transparent, not opaque green
    }

    [Fact]
    public void SoftAlphaSurvivesTheSheetPath()
    {
        // A GIF's alpha is only ever 0 or 255, so the frame path hardens it.
        // A PNG sheet's is not, and 32-bit color is meant to keep every pixel
        // exactly, so the sheet path must not flatten the ramp.
        const int w = 30, h = 30;
        var bgra = new byte[w * h * 4];
        for (int y = 5; y < 25; y++)
            for (int x = 5; x < 25; x++)
            {
                int at = (y * w + x) * 4;
                bgra[at + 2] = 255; bgra[at + 3] = 100; // a soft edge, above the threshold
            }
        var img = Image(bgra, w, h);

        var sprite = SheetImport.ToSprites(img, SheetImport.Detect(img), ImportPlan.TrueColor32(), out _)[0];

        Assert.Equal(100, sprite.Direct![3]);
    }

    [Fact]
    public void ACallerSuppliedBackgroundOverridesTheGuess()
    {
        // The sheet dialog lets a user correct the guess, so detection has to
        // take an answer and report it as the caller's rather than its own.
        const int w = 80, h = 40;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i + 1] = 255; bgra[i + 3] = 255; } // green key
        foreach (var (x, y) in new[] { (4, 4), (44, 16) })
            for (int yy = y; yy < y + 20; yy++)
                for (int xx = x; xx < x + 20; xx++)
                {
                    int at = (yy * w + xx) * 4;
                    bgra[at + 2] = 255; bgra[at + 3] = 255;
                }
        var png = PngExport.Encode(bgra, w, h);

        // Green is the guess; blue is what the caller says instead.
        var wrong = SheetImport.Detect(png, new Rgba(0, 0, 255, 255));
        Assert.False(wrong.AssumedBackground);
        Assert.Equal(new Rgba(0, 0, 255, 255), wrong.Background);
        // Reading blue as background leaves the two blue boxes with all the
        // green field around them, which is one connected sheet-sized blob.
        Assert.Single(wrong.Cells);
        Assert.Equal(new SheetCell(0, 0, w, h), wrong.Cells[0]);

        var right = SheetImport.Detect(png, new Rgba(0, 255, 0, 255));
        Assert.Equal(2, right.Count);
        Assert.Equal(new SheetCell(4, 4, 20, 20), right.Cells[0]);
        Assert.Equal(new SheetCell(44, 16, 20, 20), right.Cells[1]);
    }

    [Fact]
    public void AnEmptyPaletteIsRefusedRatherThanThrowingPastTheEnd()
    {
        // A truncated PLTE yields a zero-entry palette; that is bad data, and
        // it should say so rather than failing inside a loop.
        var img = new Png.Image(2, 2, new byte[4], null, []);

        Assert.Throws<InvalidDataException>(() => SheetImport.Detect(img));
    }

    [Fact]
    public void CellsFromADifferentSheetAreRefused()
    {
        // A layout from another image would otherwise fail deep inside a copy
        // with an opaque out-of-range message.
        var bgra = Sheet(Rows("""
            ......aaaa..
            ......aaaa..
            """), out int w, out int h);
        var layout = new SheetLayout([new SheetCell(0, 0, w + 5, h)], null, false);

        Assert.Throws<ArgumentException>(() =>
            SheetImport.ToSprites(Image(bgra, w, h), layout, ImportPlan.TrueColor32(), out _));
    }

    [Fact]
    public void AnAssumedBackgroundWithNoColorIsRefused()
    {
        // ToString names the background whenever one was assumed.
        Assert.Throws<ArgumentException>(() =>
            new SheetLayout([new SheetCell(0, 0, 4, 4)], null, assumedBackground: true));
    }
}