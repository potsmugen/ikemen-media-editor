using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>Adding, replacing and deleting sprites, and what that does to sharing.</summary>
public class EditingTests
{
    private static SffFile MakeFile()
    {
        var pal = new Palette
        {
            Colors = [new Rgba(0, 0, 0, 0), new Rgba(255, 0, 0, 255), new Rgba(0, 255, 0, 255)],
        };
        var f = new SffFile { Palettes = [pal] };
        for (int i = 0; i < 3; i++)
            f.Sprites.Add(new Sprite
            {
                Group = 0, Index = i, Width = 2, Height = 2,
                Palette = pal, Indexed = [0, 1, 2, 1], PixelsDirty = true,
            });
        return f;
    }

    [Fact]
    public void InsertPutsTheSpriteWhereAsked()
    {
        var f = MakeFile();
        var s = new Sprite { Group = 7, Index = 7, Width = 1, Height = 1, Indexed = [1], PixelsDirty = true };
        f.Insert(1, s);
        Assert.Equal(1, f.PositionOf(s));
        Assert.Equal(4, f.Sprites.Count);
    }

    [Fact]
    public void DeletingGivesDependentsTheirOwnCopy()
    {
        var f = MakeFile();
        var target = f.Sprites[0];
        f.Share(f.Sprites[1], target);
        f.Share(f.Sprites[2], target);

        int detached = f.Remove(target);

        Assert.Equal(2, detached);
        Assert.Equal(2, f.Sprites.Count);
        foreach (var s in f.Sprites)
        {
            Assert.Null(s.SharesWith);
            Assert.Equal(new byte[] { 0, 1, 2, 1 }, s.Indexed); // pixels survived the delete
        }
    }

    [Fact]
    public void ImportKeepsTheSourcePaletteOrder()
    {
        // An indexed PNG whose palette is deliberately not in any natural order.
        var plte = new Rgba[] { new(0, 0, 0, 0), new(9, 9, 9, 255), new(200, 40, 40, 255), new(40, 200, 40, 255) };
        var png = PngExport.EncodeIndexed([0, 3, 2, 1], 2, 2, plte);

        var sprite = ImageImport.FromImage(png, ImportPlan.Own(), out var report);

        Assert.Equal([0, 3, 2, 1], sprite.Indexed);     // indices untouched
        Assert.Equal(plte, sprite.Palette!.Colors[..plte.Length]);
        Assert.Equal(256, sprite.Palette.Colors.Length);
        Assert.All(sprite.Palette.Colors[plte.Length..], c => Assert.Equal(new Rgba(0, 0, 0, 255), c));
        Assert.True(report.Lossless);
    }

    [Fact]
    public void ImportedTrueColorImageBuildsAPaletteInFirstSeenOrder()
    {
        // Red then green, with index 0 reserved for the mask.
        var bgra = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0 };
        var sprite = ImageImport.FromImage(PngExport.Encode(bgra, 2, 2), ImportPlan.Own(), out _);

        Assert.Equal([1, 2, 1, 0], sprite.Indexed);
        Assert.Equal(new Rgba(255, 0, 0, 255), sprite.Palette!.Colors[1]);
        Assert.Equal(new Rgba(0, 255, 0, 255), sprite.Palette.Colors[2]);
        Assert.Equal(256, sprite.Palette.Colors.Length);
        Assert.Equal(new Rgba(0, 0, 0, 255), sprite.Palette.Colors[3]);
    }

    [Fact]
    public void ImportingTooManyColorsIntoAnOwnPaletteUsesMedianCut()
    {
        const int colors = 300;
        var bgra = new byte[(colors + 1) * 4];
        for (int i = 0; i < colors; i++)
        {
            int at = (i + 1) * 4;
            bgra[at] = (byte)i;
            bgra[at + 1] = (byte)(i >> 8);
            bgra[at + 3] = 255;
        }
        var png = PngExport.Encode(bgra, colors + 1, 1);
        var prepared = ImageImport.PrepareOwnPalette(png);

        Assert.True(prepared.RequiresMedianCut);
        var sprite = prepared.Build(out var report);

        Assert.Equal(256, sprite.Palette!.Colors.Length);
        Assert.Equal(Sprite.TransparentIndex, sprite.Indexed![0]);
        Assert.All(sprite.Indexed.Skip(1), index => Assert.NotEqual(Sprite.TransparentIndex, index));
        Assert.Equal(colors, report.Colors);
        Assert.True(report.Reduced);
        Assert.False(report.Lossless);
        Assert.Contains("reduced from 300 colors to 255 colors by median cut", report.ToString());
    }

    [Fact]
    public void ImportedImageIsMatchedToThePalette()
    {
        var f = MakeFile();
        // A 2x2 PNG: transparent, exact red, exact green, near-red.
        var bgra = new byte[]
        {
            0, 0, 0, 0,
            0, 0, 255, 255,
            0, 255, 0, 255,
            0, 0, 250, 255,
        };
        var png = PngExport.Encode(bgra, 2, 2);

        var sprite = ImageImport.FromImage(png, ImportPlan.Given(f.Palettes[0], keepIndexes: false), out var report);

        Assert.Equal(2, sprite.Width);
        Assert.Equal([0, 1, 2, 1], sprite.Indexed);  // near-red snapped onto red
        Assert.Equal(3, report.Colors);
        Assert.Equal(2, report.ExactMatches);
        Assert.False(report.Lossless);
        Assert.True(sprite.PixelsDirty);
    }

    [Fact]
    public void MaskColorIsTheMaskOnlyInImagesWithoutTransparency()
    {
        var pal = new Palette { Colors = [new(255, 0, 255, 0), new(255, 0, 0, 255), new(0, 255, 0, 255)] };
        // Opaque magenta (the mask color's RGB) and red; the second image adds a transparent pixel.
        var opaque = PngExport.Encode(new byte[] { 255, 0, 255, 255, 0, 0, 255, 255 }, 2, 1);
        var withAlpha = PngExport.Encode(new byte[] { 255, 0, 255, 255, 0, 0, 255, 255, 0, 0, 0, 0 }, 3, 1);

        var masked = ImageImport.FromImage(opaque, ImportPlan.Given(pal, keepIndexes: false), out var report);
        var byAlpha = ImageImport.FromImage(withAlpha, ImportPlan.Given(pal, keepIndexes: false), out _);

        Assert.Equal([0, 1], masked.Indexed);
        Assert.Equal(report.Colors, report.ExactMatches);
        Assert.Equal([1, 1, 0], byAlpha.Indexed); // alpha is the mask; magenta is just a color
    }

    [Fact]
    public void ReplacingStopsSharing()
    {
        var f = MakeFile();
        f.Share(f.Sprites[1], f.Sprites[0]);

        var png = PngExport.Encode(new byte[] { 0, 0, 255, 255 }, 1, 1);
        ImageImport.ReplaceImage(f.Sprites[1], png, ImportPlan.Given(f.Palettes[0], keepIndexes: false));

        Assert.Null(f.Sprites[1].SharesWith);
        Assert.Equal(1, f.Sprites[1].Width);
        Assert.Equal(2, f.Sprites[0].Width); // the old target is untouched
    }

    [Fact]
    public void FailedReplacementLeavesTheSpriteUnchanged()
    {
        var f = MakeFile();
        var sprite = f.Sprites[1];
        f.Share(sprite, f.Sprites[0]);
        sprite.DecodeError = "existing error";
        sprite.PixelsDirty = false;
        var oldPixels = sprite.Indexed;
        var oldPalette = sprite.Palette;
        var oldSharesWith = sprite.SharesWith;
        var oldFormat = sprite.SrcFormat;
        var oldDepth = sprite.SrcDepth;
        var png = PngExport.Encode(new byte[] { 0, 0, 255, 255 }, 1, 1);

        Assert.Throws<InvalidOperationException>(() =>
            ImageImport.ReplaceImage(sprite, png, new ImportPlan()));

        Assert.Same(oldSharesWith, sprite.SharesWith);
        Assert.Equal(2, sprite.Width);
        Assert.Equal(2, sprite.Height);
        Assert.Same(oldPixels, sprite.Indexed);
        Assert.Same(oldPalette, sprite.Palette);
        Assert.Equal("existing error", sprite.DecodeError);
        Assert.False(sprite.PixelsDirty);
        Assert.Equal(oldFormat, sprite.SrcFormat);
        Assert.Equal(oldDepth, sprite.SrcDepth);
    }

    /// <summary>
    /// The workflow that matters: export a sprite, bring it back, and find the
    /// indices and palette order exactly as they were.
    /// </summary>
    [Fact]
    public void ExportThenImportPreservesIndicesAndOrder()
    {
        var f = MakeFile();
        var original = f.Sprites[0];
        var png = PngExport.Encode(original, original.Palette, showMask: false);

        var reimported = ImageImport.FromImage(png, ImportPlan.Own(), out _);

        Assert.Equal(original.Indexed, reimported.Indexed);
        for (int i = 1; i < original.Palette!.Colors.Length; i++)
            Assert.Equal(original.Palette.Colors[i], reimported.Palette!.Colors[i]);
    }

    [Fact]
    public void EditedFileSurvivesASaveRoundTrip()
    {
        var f = MakeFile();
        f.Share(f.Sprites[2], f.Sprites[0]);
        var png = PngExport.Encode(new byte[] { 0, 255, 0, 255 }, 1, 1);
        f.Insert(1, ImageImport.FromImage(png, ImportPlan.Given(f.Palettes[0], keepIndexes: false), out _));
        f.Remove(f.Sprites[3]);

        var tmp = Path.Combine(Path.GetTempPath(), $"edit-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var reloaded = SffFile.Open(tmp);
            Assert.Equal(f.Sprites.Count, reloaded.Sprites.Count);
            for (int i = 0; i < f.Sprites.Count; i++)
            {
                Assert.Null(reloaded.Sprites[i].DecodeError);
                Assert.Equal(f.Sprites[i].Indexed, reloaded.Sprites[i].Indexed);
            }
        }
        finally { File.Delete(tmp); }
    }
}
