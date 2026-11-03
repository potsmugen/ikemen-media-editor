using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// SFF v1 is a linked list of PCX subfiles rather than the node tables v2 uses.
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
    /// flagged. As in MUGEN and Ikemen GO, byte 32 changes nothing: flagged sprites take the previous palette.</summary>
    [Fact]
    public void SharedFileGivesFlaggedSpritesThePreviousPalette()
    {
        var f = ReadV1(1, (9000, 0, false), (9000, 1, false), (0, 0, true), (0, 1, true));
        Assert.Equal(1, f.V1PaletteType);
        Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        Assert.Same(f.Sprites[1].Palette, f.Sprites[2].Palette);
        Assert.Same(f.Sprites[1].Palette, f.Sprites[3].Palette);
    }

    /// <summary>Shared file shaped like Donald: an unflagged 0,0 keeps its stored palette,
    /// and the flagged sprites after it take that one.</summary>
    [Fact]
    public void SharedFileGivesFlaggedSpritesAnUnflaggedSpritesPalette()
    {
        var f = ReadV1(1, (9000, 0, false), (0, 0, false), (0, 1, true));
        Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        Assert.Same(f.Sprites[1].Palette, f.Sprites[2].Palette);
    }

    /// <summary>Individual file: a flagged sprite (SprMaker -p) takes the previous palette.</summary>
    [Fact]
    public void IndividualFileGivesFlaggedSpritesThePreviousPalette()
    {
        var f = ReadV1(0, (9000, 0, false), (9000, 1, false), (0, 0, true));
        Assert.Equal(0, f.V1PaletteType);
        Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        Assert.Same(f.Sprites[1].Palette, f.Sprites[2].Palette);
    }

    /// <summary>A flagged first sprite has no palette to reuse, so it reads its own, as in MUGEN and Ikemen GO.</summary>
    [Fact]
    public void FlaggedFirstSpriteReadsItsOwnPalette()
    {
        var f = ReadV1(0, (0, 0, true), (0, 1, true));
        Assert.Single(f.Palettes);
        Assert.Same(f.Palettes[0], f.Sprites[0].Palette);
        Assert.Same(f.Palettes[0], f.Sprites[1].Palette);
        Assert.Equal(10, f.Palettes[0].Colors[1].R);
    }

    /// <summary>A plain save writes header byte 32 as 0 (Individual) whatever was read; Save Like
    /// SprMaker 0.97 writes its own mode.</summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void PlainV1SaveWritesIndividualPaletteType(byte type)
    {
        var f = ReadV1(type, (9000, 0, false), (0, 0, true));
        Assert.Equal(type, f.V1PaletteType);
        var tmp = Path.Combine(Path.GetTempPath(), $"v1type-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            Assert.Equal(0, File.ReadAllBytes(tmp)[32]);
            SprMakerSave.Save(f, tmp, new SprMakerOptions { UsePreviousPalette = true });
            Assert.Equal(1, File.ReadAllBytes(tmp)[32]);
            SprMakerSave.Save(f, tmp, new SprMakerOptions { UsePreviousPalette = false });
            Assert.Equal(0, File.ReadAllBytes(tmp)[32]);
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
    /// <summary>Each subfile's PCX bytes in list order; links have none.</summary>
    private static List<byte[]> RawPcx(string path) => RawRecords(path).Select(r => r.Pcx).ToList();

    /// <summary>Each subfile's PCX bytes and same-palette flag, in list order.</summary>
    private static List<(byte[] Pcx, bool SamePalette)> RawRecords(string path)
    {
        var b = File.ReadAllBytes(path);
        var list = new List<(byte[], bool)>();
        uint off = BitConverter.ToUInt32(b, 24);
        while (off != 0 && off + 32 <= b.Length)
        {
            uint next = BitConverter.ToUInt32(b, (int)off), length = BitConverter.ToUInt32(b, (int)off + 4);
            // Trimmed like the reader: kfm - old.sff's last record claims the 768 palette bytes SprMaker dropped.
            length = (uint)Math.Min(length, b.Length - (off + 32));
            list.Add((b.AsSpan((int)off + 32, (int)length).ToArray(), b[off + 18] != 0));
            if (next <= off) break;
            off = next;
        }
        return list;
    }

    /// <summary>A plain v1 save keeps every untouched PCX as read: header fields (DPI, 16-color
    /// header palette) and palette bytes left in same-palette records survive. A record whose flag
    /// the save changes may need its own palette. Links have no PCX and keep their flag.</summary>
    [Theory]
    [InlineData("kfm-v1.sff")]
    [InlineData("kfm - old.sff")]
    public void PlainV1SaveKeepsUntouchedPcxBytes(string name)
    {
        var path = WorkingFiles.Find(name);
        if (path is null) return;

        var before = RawRecords(path);
        var tmp = Path.Combine(Path.GetTempPath(), $"v1keep-{Guid.NewGuid():N}.sff");
        try
        {
            SffFile.Open(path).Save(tmp, SffVersions.V1);
            var after = RawRecords(tmp);
            Assert.Equal(before.Count, after.Count);
            int kept = 0, images = 0;
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i].Pcx.Length == 0) { Assert.Equal(before[i].SamePalette, after[i].SamePalette); continue; }
                images++;
                if (before[i].SamePalette == after[i].SamePalette) { Assert.Equal(before[i].Pcx, after[i].Pcx); kept++; }
            }
            Assert.True(kept >= images - 1, $"only {kept} of {images} images kept their flag");
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>An edited sprite is re-encoded; the rest keep their bytes.</summary>
    [Fact]
    public void PlainV1SaveReencodesOnlyEditedSprites()
    {
        var path = Sample();
        if (path is null) return;

        var f = SffFile.Open(path);
        int k = f.Sprites.FindIndex(s => s.SharesWith is null && s.Width * s.Height > 1);
        var edited = f.Sprites[k].Indexed!.ToArray();
        edited[0] = (byte)(edited[0] == 7 ? 8 : 7);
        f.Sprites[k].Indexed = edited;
        f.Sprites[k].PixelsDirty = true;

        var before = RawPcx(path);
        var tmp = Path.Combine(Path.GetTempPath(), $"v1edit-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            var after = RawPcx(tmp);
            for (int i = 0; i < before.Count; i++)
                if (i == k) Assert.NotEqual(before[i], after[i]);
                else Assert.Equal(before[i], after[i]);
            Assert.Equal(edited, SffFile.Open(tmp).Sprites[k].Indexed);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>A palette edit re-encodes the record that carries that palette, so the color reloads.</summary>
    [Fact]
    public void PlainV1SaveReencodesSpriteWhosePaletteChanged()
    {
        var path = Sample();
        if (path is null) return;

        var f = SffFile.Open(path);
        int k = f.Sprites.FindIndex(s => s.SharesWith is null);
        var pal = f.Sprites[k].Palette!;
        var old = pal.Colors[5];
        pal.Colors[5] = new Rgba((byte)(old.R ^ 0xff), old.G, old.B, old.A);

        var tmp = Path.Combine(Path.GetTempPath(), $"v1pal-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            Assert.NotEqual(RawPcx(path)[k], RawPcx(tmp)[k]);
            Assert.Equal(pal.Colors[5].R, SffFile.Open(tmp).Sprites[k].Palette!.Colors[5].R);
        }
        finally { File.Delete(tmp); }
    }
}
