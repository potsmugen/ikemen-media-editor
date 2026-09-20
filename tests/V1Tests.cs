using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// SFFv1 is a linked list of PCX subfiles rather than the node tables v2 uses.
/// Needs working/kfm-v1.sff; skips without it.
/// </summary>
public class V1Tests
{
    private static string? Sample() => WorkingFiles.Find("kfm-v1.sff");

    [Fact]
    public void ReadsEverySpriteAsEightBitPcx()
    {
        var path = Sample();
        if (path is null) return;

        var f = SffFile.Open(path);
        Assert.Equal(1, f.Version.Hi);
        Assert.NotEmpty(f.Sprites);

        foreach (var s in f.Sprites)
        {
            Assert.Null(s.DecodeError);
            Assert.Equal(SpriteFormat.Pcx, s.SrcFormat);
            Assert.NotNull(s.Indexed);
            Assert.Equal(s.Width * s.Height, s.Indexed!.Length);
            Assert.NotNull(s.Palette);
        }
    }

    /// <summary>
    /// Most v1 sprites set the "same palette" flag and carry no palette of their
    /// own, so only a handful of distinct tables should come out of a character.
    /// </summary>
    [Fact]
    public void PalettesAreSharedRatherThanDuplicated()
    {
        var path = Sample();
        if (path is null) return;

        var f = SffFile.Open(path);
        Assert.NotEmpty(f.Palettes);
        Assert.True(f.Palettes.Count < f.Sprites.Count / 4,
            $"{f.Palettes.Count} palettes for {f.Sprites.Count} sprites suggests the same-palette flag is being ignored");
        foreach (var p in f.Palettes) Assert.Equal(256, p.Colors.Length);
    }

    [Fact]
    public void ReadsSamePaletteSpriteWhosePixelsEndWithUnmarkedPaletteBytes()
    {
        var path = WorkingFiles.Find("kfm - old.sff");
        if (path is null) return;

        var file = SffFile.Open(path);

        Assert.Null(file.Sprites[252].DecodeError);
        Assert.Equal(57, file.Sprites[252].Width);
        Assert.Equal(115, file.Sprites[252].Height);
        Assert.NotNull(file.Sprites[252].Indexed);
        Assert.Equal(57 * 115, file.Sprites[252].Indexed!.Length);
        Assert.Same(file.Sprites[251].Palette, file.Sprites[252].Palette);
    }

    [Fact]
    public void HeaderPaletteTypeDoesNotOverridePerSpritePaletteFlags()
    {
        static Palette MakePalette(byte red) => new()
        {
            Colors = Enumerable.Range(0, 256).Select(i => new Rgba(red, (byte)i, 0, 255)).ToArray(),
        };

        var first = MakePalette(10);
        var second = MakePalette(90);
        var third = MakePalette(170);
        var f = new SffFile { Version = SffVersions.V1, Palettes = [first, second, third] };
        f.Sprites.Add(new Sprite { Group = 1, Index = 0, Width = 1, Height = 1, Palette = first, Indexed = [1] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 1, Width = 1, Height = 1, Palette = second, Indexed = [2] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 2, Width = 1, Height = 1, Palette = third, Indexed = [3] });

        var tmp = Path.Combine(Path.GetTempPath(), $"v1pal-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            var bytes = File.ReadAllBytes(tmp);
            bytes[32] = 1;
            uint firstNode = BitConverter.ToUInt32(bytes, 24);
            uint secondNode = BitConverter.ToUInt32(bytes, (int)firstNode);
            uint thirdNode = BitConverter.ToUInt32(bytes, (int)secondNode);
            bytes[(int)thirdNode + 18] = 1;
            File.WriteAllBytes(tmp, bytes);

            var saved = SffFile.Open(tmp);
            Assert.NotSame(saved.Sprites[0].Palette, saved.Sprites[1].Palette);
            Assert.Same(saved.Sprites[1].Palette, saved.Sprites[2].Palette);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>Writing v1 back out and reading it again keeps every sprite.</summary>
    [Fact]
    public void RoundTripsThroughV1()
    {
        var path = Sample();
        if (path is null) return;

        var original = SffFile.Open(path);
        var tmp = Path.Combine(Path.GetTempPath(), $"v1rt-{Guid.NewGuid():N}.sff");
        try
        {
            original.Save(tmp, SffVersions.V1);
            var reloaded = SffFile.Open(tmp);

            Assert.Equal(1, reloaded.Version.Hi);
            Assert.Equal(original.Sprites.Count, reloaded.Sprites.Count);
            for (int i = 0; i < original.Sprites.Count; i++)
            {
                var (a, b) = (original.Sprites[i], reloaded.Sprites[i]);
                Assert.Null(b.DecodeError);
                Assert.Equal(a.Group, b.Group);
                Assert.Equal(a.Index, b.Index);
                Assert.Equal(a.AxisX, b.AxisX);
                Assert.Equal(a.AxisY, b.AxisY);
                Assert.Equal(a.Indexed, b.Indexed);
                Assert.Equal(a.SharesWith is not null, b.SharesWith is not null);
            }
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>Opening v1 and saving produces a readable v2 file with the same pixels.</summary>
    [Fact]
    public void ConvertsToV2OnSave()
    {
        var path = Sample();
        if (path is null) return;

        var original = SffFile.Open(path);
        var tmp = Path.Combine(Path.GetTempPath(), $"v1to2-{Guid.NewGuid():N}.sff");
        try
        {
            original.Save(tmp);
            var converted = SffFile.Open(tmp);

            Assert.Equal(2, converted.Version.Hi);
            Assert.Equal(original.Sprites.Count, converted.Sprites.Count);
            // Conversion rekeys the SFF's own palettes to group 2, indexes unchanged.
            Assert.Equal(original.Palettes.Count, converted.Palettes.Count);
            // The table is written in key order, so match by index, not position.
            Assert.All(converted.Palettes, p => Assert.Equal(SffWriter.V1ConvertedPaletteGroup, p.Group));
            foreach (var o in original.Palettes)
                Assert.Contains(converted.Palettes, c => c.Index == o.Index && c.Colors.SequenceEqual(o.Colors));
            for (int i = 0; i < original.Sprites.Count; i++)
            {
                Assert.Null(converted.Sprites[i].DecodeError);
                Assert.Equal(original.Sprites[i].Group, converted.Sprites[i].Group);
                Assert.Equal(original.Sprites[i].Index, converted.Sprites[i].Index);
                Assert.Equal(original.Sprites[i].Indexed, converted.Sprites[i].Indexed);
            }
        }
        finally { File.Delete(tmp); }
    }
}
