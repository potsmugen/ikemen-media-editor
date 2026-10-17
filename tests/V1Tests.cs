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

    /// <summary>Saves one sprite per entry, each with its own distinct palette, then sets header
    /// byte 32 and the flagged sprites' same-palette byte, so only the reader decides their palettes.</summary>
    private static SffFile ReadV1(byte headerPaletteType, params (int Group, int Index, bool Flagged)[] sprites)
    {
        var f = new SffFile { Version = SffVersions.V1 };
        for (int i = 0; i < sprites.Length; i++)
        {
            var pal = new Palette
            {
                Colors = Enumerable.Range(0, 256).Select(c => new Rgba((byte)(10 + i * 40), (byte)c, 0, 255)).ToArray(),
            };
            f.Palettes.Add(pal);
            f.Sprites.Add(new Sprite { Group = sprites[i].Group, Index = sprites[i].Index, Width = 1, Height = 1, Palette = pal, Indexed = [1] });
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"v1pal-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            var bytes = File.ReadAllBytes(tmp);
            bytes[32] = headerPaletteType;
            uint node = BitConverter.ToUInt32(bytes, 24);
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites[i].Flagged) bytes[(int)node + 18] = 1;
                node = BitConverter.ToUInt32(bytes, (int)node);
            }
            File.WriteAllBytes(tmp, bytes);
            return SffFile.Open(tmp);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>Shared file shaped like kfm_win: 9000,1 has its own palette and 0,0 follows it
    /// flagged, yet 0,0 and later flagged sprites take 9000,0's (the first) palette.</summary>
    [Fact]
    public void SharedFileGivesFlaggedSpritesTheFirstPalette()
    {
        var f = ReadV1(1, (9000, 0, false), (9000, 1, false), (0, 0, true), (0, 1, true));
        Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        Assert.Same(f.Sprites[0].Palette, f.Sprites[2].Palette);
        Assert.Same(f.Sprites[0].Palette, f.Sprites[3].Palette);
    }

    /// <summary>Shared file shaped like Donald: an unflagged 0,0 keeps its stored palette;
    /// the flagged sprites after it still take the first palette.</summary>
    [Fact]
    public void SharedFileKeepsUnflaggedSpritePalettes()
    {
        var f = ReadV1(1, (9000, 0, false), (0, 0, false), (0, 1, true));
        Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        Assert.Same(f.Sprites[0].Palette, f.Sprites[2].Palette);
    }

    /// <summary>Individual file: a flagged sprite (SprMaker -p) takes the previous palette.</summary>
    [Fact]
    public void IndividualFileGivesFlaggedSpritesThePreviousPalette()
    {
        var f = ReadV1(0, (9000, 0, false), (9000, 1, false), (0, 0, true));
        Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        Assert.Same(f.Sprites[1].Palette, f.Sprites[2].Palette);
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
            // Conversion rekeys the SFF's own palettes; the table is written in key order, so match by key.
            Assert.Equal(original.Palettes.Count, converted.Palettes.Count);
            foreach (var (o, key) in SffWriter.V1ConvertedKeys(original))
                Assert.Contains(converted.Palettes, c => (c.Group, c.Index) == key && c.Colors.SequenceEqual(o.Colors));
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
