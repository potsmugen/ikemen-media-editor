using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Crash recovery stores a v1 session holding true-color sprites as v2 by setting
/// Version to 2.0.1.0 for the write. That must not move palettes the way a real
/// v1 to v2 conversion does, or the recovered session would differ.
/// </summary>
public class RecoverySnapshotTests
{
    private static SffFile V1SessionWithTrueColor()
    {
        var colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray();
        var a = new Palette { Group = 1, Index = 1, Colors = colors };
        var b = new Palette { Group = 1, Index = 2, Colors = colors.Reverse().ToArray() };
        var f = new SffFile { Version = SffVersions.V1, CompatVersion = SffVersions.V1, Palettes = [a, b] };
        f.Sprites.Add(new Sprite { Group = 0, Index = 0, Width = 2, Height = 2, Palette = a, Indexed = [0, 1, 2, 3], PixelsDirty = true });
        f.Sprites.Add(new Sprite { Group = 0, Index = 1, Width = 1, Height = 1, Palette = b, Direct = [10, 20, 30, 255], PixelsDirty = true });
        return f;
    }

    [Fact]
    public void FitsV1_FalseOnlyWithTrueColor()
    {
        var f = V1SessionWithTrueColor();
        Assert.False(SffWriter.FitsV1(f));
        f.Sprites.RemoveAt(1);
        Assert.True(SffWriter.FitsV1(f));
    }

    [Fact]
    public void V2SnapshotOfV1Session_KeepsPaletteKeysAndPixels()
    {
        var f = V1SessionWithTrueColor();
        string tmp = Path.Combine(Path.GetTempPath(), $"recv1-{Guid.NewGuid():N}.sff");
        try
        {
            var version = f.Version;
            f.Version = SffVersions.V2_1;
            f.Save(tmp, f.Version);
            f.Version = version;

            var back = SffFile.Open(tmp);
            Assert.Equal(new[] { (1, 1), (1, 2) }, back.Palettes.Select(p => (p.Group, p.Index)));
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, back.Sprites[0].Indexed);
            Assert.Equal(new byte[] { 10, 20, 30, 255 }, back.Sprites[1].Direct);
            Assert.Equal((1, 2), (back.Sprites[1].Palette!.Group, back.Sprites[1].Palette!.Index));

            // Without the switch the writer converts: 0,0's palette takes 1,1, the other its sprite's group.
            f.Save(tmp, SffVersions.V2_1);
            Assert.Equal(new[] { (0, 0), (1, 1) }, SffFile.Open(tmp).Palettes.Select(p => (p.Group, p.Index)));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
