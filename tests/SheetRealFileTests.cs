using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Detection run against a real sheet rather than hand-built pixels. The unit
/// tests can only prove the shapes they were written for; a sheet from another
/// tool is where a wrong assumption about real files shows up. The sheet is not
/// checked in, so these skip when it is absent.
/// </summary>
[Collection(SheetImportCollection.Name)]
public class SheetRealFileTests : IDisposable
{
    public void Dispose() => SheetImport.MinimumSize = SheetImport.DefaultMinimumSize;

    /// <summary>working/zangief_sheet.png, or null when absent and the tests skip.</summary>
    private static byte[]? Zangief() =>
        WorkingFiles.Find("zangief_sheet.png") is { } path ? File.ReadAllBytes(path) : null;

    [Fact]
    public void ChromaKeyedSheetFindsItsSpritesAgainstTheKeyColor()
    {
        if (Zangief() is not { } bytes) return;

        var layout = SheetImport.Detect(bytes);

        // A green-screen keyed sheet rather than one carrying alpha, so the key
        // color is the guess the design calls for, and it has to be reported.
        Assert.True(layout.AssumedBackground);
        Assert.Equal(new Rgba(0, 255, 0, 255), layout.Background);
        Assert.Contains("as the background", layout.ToString());

        // Every pose is its own component. The count moves with the file, so
        // this only asserts there is a sheet's worth of sprites rather than
        // pinning today's number.
        Assert.True(layout.Count > 100, $"only found {layout.Count}");
    }

    [Fact]
    public void TheDefaultFloorDropsKeyedEdgeSpeckleButKeepsThePoses()
    {
        if (Zangief() is not { } bytes) return;

        SheetImport.MinimumSize = SheetImport.DefaultMinimumSize;
        var withFloor = SheetImport.Detect(bytes);
        int kept = withFloor.Count;

        SheetImport.MinimumSize = 0;
        int everything = SheetImport.Detect(bytes).Count;

        // What makes 16 the default: on this sheet every stray pixel is under
        // 12px and the smallest real pose is 30px, so the floor drops the
        // speckle and keeps the poses.
        Assert.True(kept < everything, "the floor should drop something");
        Assert.True(kept > 100, $"floor left only {kept}");
        Assert.All(withFloor.Cells,
            c => Assert.True(Math.Max(c.Width, c.Height) >= SheetImport.DefaultMinimumSize));
    }

    [Fact]
    public void ChromaKeyedSheetBuildsSpritesThroughTheImportPath()
    {
        if (Zangief() is not { } bytes) return;

        var layout = SheetImport.Detect(bytes);
        var sprites = SheetImport.ToSprites(bytes, layout, ImportPlan.TrueColor32(), out var reports);

        Assert.Equal(layout.Count, sprites.Count);
        Assert.Equal(layout.Count, reports.Count);
        Assert.All(sprites, s =>
        {
            Assert.True(s.Width > 0 && s.Height > 0);
            Assert.Equal(s.Width * s.Height * 4, s.Direct!.Length);
        });

        // Cells come back in the order the layout found them, so sprite numbers
        // follow the sheet top to bottom.
        Assert.Equal((layout.Cells[0].Width, layout.Cells[0].Height), (sprites[0].Width, sprites[0].Height));
    }

    [Fact]
    public void TheKeyColorIsNotBakedIntoAnyImportedSprite()
    {
        if (Zangief() is not { } bytes) return;

        var layout = SheetImport.Detect(bytes);
        var sprites = SheetImport.ToSprites(bytes, layout, ImportPlan.TrueColor32(), out _);

        // The point of clearing the guessed background inside each crop: a key
        // color showing through a pose's gaps must not survive as picture.
        var green = new Rgba(0, 255, 0, 255);
        foreach (var s in sprites)
        {
            var direct = s.Direct!;
            for (int i = 0; i + 3 < direct.Length; i += 4)
                Assert.False(direct[i + 3] != 0 && direct[i + 1] == green.G && direct[i] == green.B && direct[i + 2] == green.R,
                    $"sprite {s.Width}x{s.Height} still carries opaque key color at pixel {i / 4}");
        }
    }
}