using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Engines ignore a true-color node's palette number, so a true-color sprite must not count as using a palette.
/// The number is still written back as read
/// </summary>
public class TrueColorPaletteTests
{
    private static SffFile WithTrueColor()
    {
        var colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray();
        var a = new Palette { Group = 1, Index = 1, Colors = colors };
        var b = new Palette { Group = 1, Index = 2, Colors = colors.Reverse().ToArray() };
        var f = new SffFile { Version = SffVersions.V2_1, CompatVersion = SffVersions.V2_1, Palettes = [a, b] };
        f.Sprites.Add(new Sprite { Group = 0, Index = 0, Width = 2, Height = 2, Palette = a, Indexed = [0, 1, 2, 3], PixelsDirty = true });
        f.Sprites.Add(new Sprite { Group = 0, Index = 1, Width = 1, Height = 1, TrueColorPaletteField = 1, Direct = [10, 20, 30, 255], PixelsDirty = true });
        return f;
    }

    [Fact]
    public void Open_TrueColorSpriteUsesNoPalette_AndKeepsItsNumber()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"truecolor-pal-{Guid.NewGuid():N}.sff");
        try
        {
            WithTrueColor().Save(tmp, SffVersions.V2_1);
            var back = SffFile.Open(tmp);
            Assert.Null(back.Sprites[1].Palette);
            Assert.Equal(1, back.Sprites[1].TrueColorPaletteField);

            // Saved again, the node keeps the number it was read with
            back.Save(tmp, SffVersions.V2_1);
            Assert.Equal(1, SffFile.Open(tmp).Sprites[1].TrueColorPaletteField);

            // Recovery snapshots link it anyway, so a session comes back as it was
            var recovered = SffFile.Open(tmp, keepTrueColorPalettes: true);
            Assert.Same(recovered.Palettes[1], recovered.Sprites[1].Palette);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
