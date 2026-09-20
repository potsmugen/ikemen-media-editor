using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>PCX as an import and export format for sprites, alongside PNG.</summary>
public class PcxTests
{
    private static string? RealSff() => WorkingFiles.Find("kfm-v2.sff");

    private static Rgba C(byte v) => new(v, v, v, 255);

    private static Sprite MakeSprite() => new()
    {
        Group = 0, Index = 0, Width = 2, Height = 2,
        Indexed = [0, 1, 2, 1],
        Palette = new Palette { Group = 1, Index = 1, Colors = [new(0, 0, 0, 0), C(10), C(20)] },
        PixelsDirty = true,
    };

    [Fact]
    public void DecodeToBgraReadsPcxPixelsAndPalette()
    {
        var palette = new Palette { Colors = [new(0, 0, 0, 0), new(10, 20, 30, 255)] };
        var pcx = PcxExport.EncodeIndexed([0, 1], 2, 1, palette);

        var (bgra, width, height) = ImageImport.DecodeToBgra(pcx);

        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 30, 20, 10, 255 }, bgra);
    }

    [Fact]
    public void ExportThenImportPreservesIndexesAndPalette()
    {
        var sprite = MakeSprite();
        var pcx = PcxExport.Encode(sprite, sprite.Palette);

        var back = ImageImport.FromImage(pcx, ImportPlan.Own(), out var report);

        Assert.Equal(sprite.Indexed, back.Indexed);
        // PCX always writes a 256-entry table; the colors we set must survive.
        for (int i = 0; i < sprite.Palette!.Colors.Length; i++)
            Assert.Equal(sprite.Palette.Colors[i], back.Palette!.Colors[i]);
        Assert.True(report.Lossless);
    }

    [Fact]
    public void RealSffSpritesSurvivePcxExportAndImport()
    {
        var path = RealSff();
        if (path is null) return;

        var file = SffFile.Open(path);
        Assert.NotEmpty(file.Sprites);
        int checkedSprites = 0;
        foreach (var sprite in file.Sprites)
        {
            // Linked v1 entries have no image payload of their own; their
            // target is checked when it appears in the list.
            if (sprite.Indexed is null || sprite.Palette is null) continue;

            var pcx = PcxExport.Encode(sprite, sprite.Palette);
            Assert.True(ImageImport.ImageIsIndexed(pcx));
            var imported = ImageImport.FromImage(pcx, ImportPlan.Own(), out var report);

            Assert.Equal(sprite.Width, imported.Width);
            Assert.Equal(sprite.Height, imported.Height);
            Assert.Equal(sprite.Indexed, imported.Indexed);
            Assert.True(report.Lossless, $"sprite {sprite.Group},{sprite.Index} was not lossless");
            checkedSprites++;
        }

        Assert.True(checkedSprites > 10, $"only checked {checkedSprites} real sprites");
    }

    [Fact]
    public void ExplicitCanvasPcxKeepsPaddedIndexes()
    {
        byte[] indices = [0, 1, 0, 2, 0, 1];
        var palette = new Palette { Colors = [new(0, 0, 0, 0), C(10), C(20)] };

        var pcx = PcxExport.EncodeIndexed(indices, 3, 2, palette);
        var back = ImageImport.FromImage(pcx, ImportPlan.Own(), out _);

        Assert.Equal(3, back.Width);
        Assert.Equal(2, back.Height);
        Assert.Equal(indices, back.Indexed);
    }

    [Fact]
    public void PcxCountsAsIndexedPngDoesNotAlways()
    {
        var sprite = MakeSprite();

        Assert.True(ImageImport.ImageIsIndexed(PcxExport.Encode(sprite, sprite.Palette)));
        Assert.True(ImageImport.ImageIsIndexed(PngExport.EncodeIndexed([0, 1], 2, 1, [new(0, 0, 0, 0), C(1)])));
        Assert.False(ImageImport.ImageIsIndexed(PngExport.Encode([1, 2, 3, 255, 4, 5, 6, 255], 2, 1)));
        Assert.False(ImageImport.ImageIsIndexed([1, 2, 3]));
    }

    [Fact]
    public void PcxPixelsCanBeMatchedToAGivenPalette()
    {
        var sprite = MakeSprite();
        var pcx = PcxExport.Encode(sprite, sprite.Palette);
        var target = new Palette { Colors = [new(0, 0, 0, 0), C(11), C(19)] };

        var back = ImageImport.FromImage(pcx, ImportPlan.Given(target, keepIndexes: false), out _);

        Assert.Same(target, back.Palette);
        Assert.Equal([0, 1, 2, 1], back.Indexed);  // nearest colors land on the same indexes
    }

    [Fact]
    public void PcxCanBeImportedAsTrueColor()
    {
        var sprite = MakeSprite();
        var pcx = PcxExport.Encode(sprite, sprite.Palette);

        var back = ImageImport.FromImage(pcx, ImportPlan.TrueColor32(), out _);

        Assert.Null(back.Indexed);
        Assert.NotNull(back.Direct);
    }

    [Fact]
    public void DecodeRejectsTruncatedScanlineBeforePalette()
    {
        var pcx = new byte[128 + 3 + 769];
        pcx[0] = 0x0a;
        pcx[2] = 0;
        pcx[3] = 8;
        pcx[8] = 1;
        pcx[10] = 1;
        pcx[65] = 1;
        pcx[66] = 2;
        pcx[128] = 1;
        pcx[129] = 2;
        pcx[130] = 3;
        pcx[131] = 0x0c;

        Assert.Throws<InvalidDataException>(() => Pcx.Decode(pcx));
    }

    [Fact]
    public void DecodeSamePalettePcxDoesNotAssumeAnUnmarkedTrailingPalette()
    {
        var pcx = new byte[128 + 900];
        pcx[0] = 0x0a;
        pcx[2] = 0;
        pcx[3] = 8;
        pcx[8] = 0x83;
        pcx[9] = 0x03;
        pcx[10] = 0;
        pcx[65] = 1;
        pcx[66] = 0x84;
        pcx[67] = 0x03;
        for (int i = 0; i < 900; i++) pcx[128 + i] = (byte)i;

        var image = Pcx.Decode(pcx, samePalette: true);

        Assert.Equal(900, image.Width);
        Assert.Equal(Enumerable.Range(0, 900).Select(i => (byte)i), image.Indices);
        Assert.Null(image.Palette);
    }

    [Fact]
    public void PcxWithoutItsOwnPaletteOnlyImportsByKeptIndexes()
    {
        // A v1 "same palette as previous" sprite has no palette bytes at all:
        // a minimal 2x2 uncompressed PCX, header plus four literal indexes.
        var pcx = new byte[132];
        pcx[0] = 0x0a;              // manufacturer
        pcx[2] = 0;                 // uncompressed
        pcx[3] = 8;                 // bits per pixel
        pcx[8] = 1;                 // xmax  (width  = xmax - xmin + 1 = 2)
        pcx[10] = 1;                // ymax  (height = ymax - ymin + 1 = 2)
        pcx[65] = 1;                // one plane
        pcx[66] = 2;                // bytes per scanline
        pcx[128] = 1; pcx[129] = 1; // row 0: indexes 1, 1
        pcx[130] = 2; pcx[131] = 2; // row 1: indexes 2, 2

        Assert.Throws<InvalidDataException>(
            () => ImageImport.FromImage(pcx, ImportPlan.Own(), out _));

        var target = new Palette { Colors = [new(0, 0, 0, 0), C(9), C(8)] };
        var back = ImageImport.FromImage(pcx, ImportPlan.Given(target, keepIndexes: true), out _);
        Assert.Equal([1, 1, 2, 2], back.Indexed);
        Assert.Same(target, back.Palette);
    }

    [Fact]
    public void PcxExportRejectsWidthWhosePaddedScanlineOverflowsHeader()
    {
        var palette = new Palette { Colors = [new(0, 0, 0, 0)] };
        Assert.Throws<ArgumentOutOfRangeException>(() => PcxExport.EncodeIndexed(new byte[65535], 65535, 1, palette));
    }

    [Fact]
    public void PcxExportRefusesTrueColorSprites()
    {
        var sprite = new Sprite { Group = 0, Index = 0, Width = 1, Height = 1, Direct = [1, 2, 3, 255], PixelsDirty = true };
        Assert.Throws<NotSupportedException>(() => PcxExport.Encode(sprite, null));
    }

    [Fact]
    public void UnknownImageDataIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ImageImport.FromImage([1, 2, 3], ImportPlan.Own(), out _));
    }
}
