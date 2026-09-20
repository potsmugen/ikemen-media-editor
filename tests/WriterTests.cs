using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Save is the feature the whole editor depends on, so it is checked by writing
/// a real file back out and reading it in again. Needs working/kfm-v2.sff; skips
/// without it.
/// </summary>
public class WriterTests
{
    private static string? Sample() => WorkingFiles.Find("kfm-v2.sff");

    [Fact]
    public void Rle8_EncodeRoundTrips()
    {
        var rng = new Random(1234);
        var pix = new byte[5000];
        for (int i = 0; i < pix.Length;)
        {
            byte v = (byte)rng.Next(256);
            int run = rng.Next(1, 90);
            for (int k = 0; k < run && i < pix.Length; k++) pix[i++] = v;
        }
        var packed = Rle8.Encode(pix);
        var back = new byte[pix.Length];
        Rle8.Decode(packed, back);
        Assert.Equal(pix, back);
    }

    [Fact]
    public void OutOfRangeFieldsClampOrWrap()
    {
        foreach (var version in new[] { SffVersions.V1, SffVersions.V2 })
            foreach (bool clamp in new[] { true, false })
            {
                var pal = new Palette { Group = 1, Index = 1, Colors = [new Rgba(0, 0, 0, 0), new Rgba(255, 0, 0, 255)] };
                var f = new SffFile { Version = version, Palettes = [pal] };
                f.Sprites.Add(new Sprite
                {
                    Group = 70000, Index = -5, AxisX = 40000, AxisY = -40000,
                    Width = 1, Height = 1, Indexed = [1], PixelsDirty = true, Palette = pal,
                });
                var tmp = Path.Combine(Path.GetTempPath(), $"sfftest-{Guid.NewGuid():N}.sff");
                try
                {
                    f.Save(tmp, version, clamp);
                    var s = SffFile.Open(tmp).Sprites[0];
                    Assert.Equal(clamp ? 65535 : 70000 - 65536, s.Group);
                    Assert.Equal(clamp ? 0 : 65536 - 5, s.Index);
                    Assert.Equal(clamp ? 32767 : 40000 - 65536, s.AxisX);
                    Assert.Equal(clamp ? -32768 : 65536 - 40000, s.AxisY);
                }
                finally { File.Delete(tmp); }
            }
    }

    [Fact]
    public void UntouchedFileSurvivesSaveAndReload()
    {
        var path = Sample();
        if (path is null) return;

        var original = SffFile.Open(path);
        var tmp = Path.Combine(Path.GetTempPath(), $"sfftest-{Guid.NewGuid():N}.sff");
        try
        {
            original.Save(tmp);
            AssertSame(original, SffFile.Open(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void RepaintedSpritesReEncodeLosslessly()
    {
        var path = Sample();
        if (path is null) return;

        var original = SffFile.Open(path);
        // Pretend every indexed sprite was edited, forcing the RLE8 path.
        foreach (var s in original.Sprites)
            if (s.Indexed is not null && s.SharesWith is null) s.PixelsDirty = true;

        var tmp = Path.Combine(Path.GetTempPath(), $"sfftest-{Guid.NewGuid():N}.sff");
        try
        {
            original.Save(tmp);
            var reloaded = SffFile.Open(tmp);
            AssertSame(original, reloaded);
            Assert.Contains(reloaded.Sprites, s => s.SrcFormat == SpriteFormat.Rle8);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void EditedMetadataIsWrittenOut()
    {
        var path = Sample();
        if (path is null) return;

        var f = SffFile.Open(path);
        f.Sprites[3].Group = 1234;
        f.Sprites[3].Index = 56;
        f.Sprites[3].AxisX = -7;
        f.Sprites[3].AxisY = 99;

        var tmp = Path.Combine(Path.GetTempPath(), $"sfftest-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var s = SffFile.Open(tmp).Sprites[3];
            Assert.Equal(1234, s.Group);
            Assert.Equal(56, s.Index);
            Assert.Equal(-7, s.AxisX);
            Assert.Equal(99, s.AxisY);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ExternalPalettesEmbedWithConflictResolvedKeys()
    {
        var pal = new Palette { Group = 1, Index = 1, Colors = Enumerable.Range(0, 8).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var f = new SffFile { Version = SffVersions.V1, Palettes = [pal] };
        f.Sprites.Add(new Sprite { Width = 1, Height = 1, Palette = pal, Indexed = [1], PixelsDirty = true });
        var act1 = new Palette { Group = 1, Index = 1, Source = PaletteSource.External, SourcePath = "kfm1.act",
            Colors = Enumerable.Range(0, 8).Select(i => new Rgba(0, (byte)i, 0, 255)).ToArray() };
        var act2 = new Palette { Group = 1, Index = 5, Source = PaletteSource.External, SourcePath = "kfm2.act",
            Colors = Enumerable.Range(0, 8).Select(i => new Rgba(0, 0, (byte)i, 255)).ToArray() };
        var used = new Palette { Group = 1, Index = 5, Source = PaletteSource.External, SourcePath = "other.act",
            Colors = Enumerable.Range(0, 8).Select(i => new Rgba((byte)i, (byte)i, 0, 255)).ToArray() };
        f.Palettes.Add(act1);
        f.Palettes.Add(act2);
        f.Palettes.Add(used);
        f.Sprites.Add(new Sprite { Group = 3, Index = 0, Width = 1, Height = 1, Palette = used, Indexed = [1], PixelsDirty = true });

        // act1 and act2 are referenced by no sprite, so both would be dropped;
        // the SFF's own palette rekeys to group 2, so act1 keeps 1, while act2
        // steps over the referenced external at 1,5 to 1,6.
        var embedded = SffWriter.EmbeddableExternals(f);
        Assert.Equal(2, embedded.Count);
        Assert.Equal((act1, 1), embedded[0]);
        Assert.Equal((act2, 6), embedded[1]);

        // What the editor does on Save As: embed with the resolved keys.
        foreach (var (p, index) in embedded) { p.Index = index; p.Source = PaletteSource.Embedded; }
        var tmp = Path.Combine(Path.GetTempPath(), $"embed-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V2);
            var r = SffFile.Open(tmp);
            Assert.Equal(4, r.Palettes.Count); // key order: group 1 first
            Assert.Equal((1, 1), (r.Palettes[0].Group, r.Palettes[0].Index));
            Assert.Equal(act1.Colors, r.Palettes[0].Colors);
            Assert.Equal((1, 5), (r.Palettes[1].Group, r.Palettes[1].Index));
            Assert.Equal((1, 6), (r.Palettes[2].Group, r.Palettes[2].Index));
            Assert.Equal((2, 1), (r.Palettes[3].Group, r.Palettes[3].Index));
            Assert.Equal(pal.Colors, r.Palettes[3].Colors);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void V1ConversionRekeysPalettesToGroup2AndFreesGroup1()
    {
        var pal = new Palette { Group = 1, Index = 1, Colors = Enumerable.Range(0, 8).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var f = new SffFile { Version = SffVersions.V1, Palettes = [pal] };
        f.Sprites.Add(new Sprite { Width = 1, Height = 1, Palette = pal, Indexed = [1], PixelsDirty = true });
        var act = new Palette { Group = 1, Index = 1, Source = PaletteSource.External, SourcePath = "kfm1.act",
            Colors = Enumerable.Range(0, 8).Select(i => new Rgba(0, (byte)i, 0, 255)).ToArray() };
        f.Palettes.Add(act);

        // The SFF's own palette moves to group 2 on conversion, so the .act
        // keeps 1,1 without stepping.
        var embedded = SffWriter.EmbeddableExternals(f);
        Assert.Single(embedded);
        Assert.Equal((act, 1), embedded[0]);

        foreach (var (p, index) in embedded) { p.Index = index; p.Source = PaletteSource.Embedded; }
        var tmp = Path.Combine(Path.GetTempPath(), $"v1rekey-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V2);
            var r = SffFile.Open(tmp);
            Assert.Equal(2, r.Palettes.Count);
            Assert.Equal((1, 1), (r.Palettes[0].Group, r.Palettes[0].Index)); // key order: .act first
            Assert.Equal(act.Colors, r.Palettes[0].Colors);
            Assert.Equal((2, 1), (r.Palettes[1].Group, r.Palettes[1].Index));
            Assert.Equal(pal.Colors, r.Palettes[1].Colors);
        }
        finally { File.Delete(tmp); }
    }

    private static void AssertSame(SffFile a, SffFile b)
    {
        Assert.Equal(a.Sprites.Count, b.Sprites.Count);
        Assert.Equal(a.Palettes.Count, b.Palettes.Count);

        for (int i = 0; i < a.Palettes.Count; i++)
        {
            Assert.Equal(a.Palettes[i].Group, b.Palettes[i].Group);
            Assert.Equal(a.Palettes[i].Index, b.Palettes[i].Index);
            Assert.Equal(a.Palettes[i].Colors, b.Palettes[i].Colors);
        }

        for (int i = 0; i < a.Sprites.Count; i++)
        {
            var (x, y) = (a.Sprites[i], b.Sprites[i]);
            Assert.Null(y.DecodeError);
            Assert.Equal(x.Group, y.Group);
            Assert.Equal(x.Index, y.Index);
            Assert.Equal(x.Width, y.Width);
            Assert.Equal(x.Height, y.Height);
            Assert.Equal(x.AxisX, y.AxisX);
            Assert.Equal(x.AxisY, y.AxisY);
            Assert.Equal(x.SharesWith is not null, y.SharesWith is not null);
            Assert.Equal(x.Indexed, y.Indexed);
            Assert.Equal(x.Direct, y.Direct);
        }
    }

    [Fact]
    public void CompatVersionFollowsSprmake2()
    {
        var pal = new Palette { Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var f = new SffFile { Palettes = [pal] };
        f.Sprites.Add(new Sprite { Width = 2, Height = 2, Palette = pal, Indexed = new byte[4], PixelsDirty = true });
        var tmp = Path.Combine(Path.GetTempPath(), $"compat-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V2_1);
            var r = SffFile.Open(tmp);
            Assert.Equal(SffVersions.V2_1, r.Version); // the chosen version
            Assert.Equal(SffVersions.V2, r.CompatVersion); // nothing needs 2.0.1.0

            f.Sprites.Add(new Sprite { Group = 1, Width = 2, Height = 2, Direct = new byte[16], PixelsDirty = true });
            f.Save(tmp, SffVersions.V2_1);
            Assert.Equal(SffVersions.V2_1, SffFile.Open(tmp).CompatVersion); // PNG32 does
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SavingBelow2010ReencodesPngSpritesAndKeepsTheVersion()
    {
        var pal = new Palette { Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var f = new SffFile { Palettes = [pal] };
        f.Sprites.Add(new Sprite
        {
            Width = 2, Height = 2, Palette = pal, Indexed = [0, 1, 2, 3],
            SrcFormat = SpriteFormat.Png8, SrcDepth = 8, SourceData = [1, 2, 3, 4, 5], PixelsDirty = false,
        });
        Assert.Equal(1, SffWriter.StoredAsPng(f));

        var tmp = Path.Combine(Path.GetTempPath(), $"png2000-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V2);
            var r = SffFile.Open(tmp);
            Assert.Equal(SffVersions.V2, r.Version);
            Assert.NotEqual(SpriteFormat.Png8, r.Sprites[0].SrcFormat);
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, r.Sprites[0].Indexed);
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData(1, 36)]
    [InlineData(2, 68)]
    public void UnusedHeaderSpaceStaysZero(int hi, int from)
    {
        var pal = new Palette { Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var f = new SffFile { Palettes = [pal] };
        f.Sprites.Add(new Sprite { Width = 2, Height = 2, Palette = pal, Indexed = new byte[4], PixelsDirty = true });
        var tmp = Path.Combine(Path.GetTempPath(), $"blank-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, hi == 1 ? SffVersions.V1 : SffVersions.V2);
            var b = File.ReadAllBytes(tmp);
            Assert.All(b[from..512], x => Assert.Equal(0, x)); // as sprmaker / sprmake2 leave it
        }
        finally { File.Delete(tmp); }
    }
}
