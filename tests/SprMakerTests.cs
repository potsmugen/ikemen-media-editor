using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class SprMakerTests
{
    private static Palette Pal(byte r) => new() { Group = 1, Index = 1, Colors = Enumerable.Range(0, 8).Select(i => new Rgba(r, (byte)i, 0, 255)).ToArray() };

    // Two identical 4x4 sprites with a one-pixel mask border, sharing one palette.
    private static SffFile Sample(SffVersion version)
    {
        var pal = Pal(10);
        var f = new SffFile { Version = version, Palettes = [pal] };
        byte[] px = [0, 0, 0, 0, 0, 1, 2, 0, 0, 3, 4, 0, 0, 0, 0, 0];
        f.Sprites.Add(new Sprite { Group = 0, Index = 0, Width = 4, Height = 4, AxisX = 2, AxisY = 2, Palette = pal, Indexed = px.ToArray(), PixelsDirty = true });
        f.Sprites.Add(new Sprite { Group = 0, Index = 1, Width = 4, Height = 4, AxisX = 5, AxisY = 5, Palette = pal, Indexed = px.ToArray(), PixelsDirty = true });
        return f;
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var f = Sample(SffVersions.V2_1);
        var linked = new Palette { Group = 1, Index = 2, LinksTo = f.Palettes[0] };
        f.Palettes.Add(linked);
        f.Sprites[1].SharesWith = f.Sprites[0];

        var c = f.Clone();
        Assert.NotSame(f.Sprites[0], c.Sprites[0]);
        Assert.Same(c.Sprites[0], c.Sprites[1].SharesWith);
        Assert.Same(c.Palettes[0], c.Sprites[0].Palette);
        Assert.Same(c.Palettes[0], c.Palettes[1].LinksTo);
        Assert.Equal(f.Palettes[0].Colors, c.Palettes[1].Colors);

        c.Sprites[0].Indexed![5] = 7;
        c.Palettes[0].Colors = [];
        Assert.Equal(1, f.Sprites[0].Indexed![5]);
        Assert.Equal(8, f.Palettes[0].Colors.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CropAndLinkApplyToTheSavedFileOnly(bool v2)
    {
        var f = Sample(v2 ? SffVersions.V2_1 : SffVersions.V1);
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            var report = v2
                ? SprMakerSave.Save(f, tmp, new SprMaker2Options { AutoCrop = true, DetectDuplicateSprites = true })
                : SprMakerSave.Save(f, tmp, new SprMakerOptions { AutoCrop = true, LinkDuplicateSprites = true });
            Assert.Equal([SprMakerEventKind.SpriteLinked, SprMakerEventKind.SpriteCropped, SprMakerEventKind.CropFollower],
                report.Events.Select(e => e.Kind));
            Assert.Equal(new SprMakerEvent(SprMakerEventKind.SpriteLinked, 0, 1, 0, 0),
                report.Events[0]);
            Assert.Equal(new SprMakerEvent(SprMakerEventKind.SpriteCropped, 0, 0,
                OldWidth: 4, OldHeight: 4, NewWidth: 2, NewHeight: 2, AxisDx: -1, AxisDy: -1),
                report.Events[1]);
            Assert.Equal(new SprMakerEvent(SprMakerEventKind.CropFollower, 0, 1, 0, 0, AxisDx: -1, AxisDy: -1),
                report.Events[2]);

            // The open file is untouched.
            Assert.All(f.Sprites, s => Assert.Equal(4, s.Width));
            Assert.Null(f.Sprites[1].SharesWith);
            Assert.Equal(5, f.Sprites[1].AxisX);

            var r = SffFile.Open(tmp);
            Assert.Equal(v2 ? 2 : 1, r.Version.Hi);
            Assert.Equal((2, 2), (r.Sprites[0].Width, r.Sprites[0].Height));
            Assert.Equal((1, 1), (r.Sprites[0].AxisX, r.Sprites[0].AxisY));
            Assert.Same(r.Sprites[0], r.Sprites[1].SharesWith);
            Assert.Equal((4, 4), (r.Sprites[1].AxisX, r.Sprites[1].AxisY)); // followed its target's crop
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SprMaker097ReusesMatchingPcxPalettesWhenRequested(bool linkDuplicatePalettes)
    {
        static Rgba[] Colors(byte redShift, byte alpha) => Enumerable.Range(0, 256)
            .Select(i => new Rgba((byte)(i + redShift), (byte)i, (byte)(255 - i), alpha)).ToArray();

        var first = new Palette { Colors = Colors(0, 255) };
        var duplicate = new Palette { Colors = Colors(0, 0) }; // PCX doesn't serialize alpha.
        var different = new Palette { Colors = Colors(1, 255) };
        var f = new SffFile { Version = SffVersions.V1, Palettes = [first, duplicate, different] };
        f.Sprites.Add(new Sprite { Group = 1, Index = 0, Width = 1, Height = 1, Palette = first, Indexed = [1] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 1, Width = 1, Height = 1, Palette = duplicate, Indexed = [2] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 2, Width = 1, Height = 1, Palette = different, Indexed = [3] });

        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            var options = new SprMakerOptions { LinkDuplicatePalettes = linkDuplicatePalettes };
            Assert.True(SprMakerOptions.SupportsLinkDuplicatePalettes);
            Assert.Empty(options.Unsupported());
            var report = SprMakerSave.Save(f, tmp, options);
            if (linkDuplicatePalettes)
                Assert.Equal([new SprMakerEvent(SprMakerEventKind.PaletteReused, 1, 1, 1, 0)], report.Events);
            else
                Assert.Empty(report.Events);

            var bytes = File.ReadAllBytes(tmp);
            Assert.Equal((byte)0, bytes[32]);
            uint firstNode = BitConverter.ToUInt32(bytes, 24);
            uint secondNode = BitConverter.ToUInt32(bytes, (int)firstNode);
            uint thirdNode = BitConverter.ToUInt32(bytes, (int)secondNode);
            Assert.Equal(linkDuplicatePalettes ? (byte)1 : (byte)0, bytes[(int)secondNode + 18]);
            Assert.Equal((byte)0, bytes[(int)thirdNode + 18]);
            uint secondLength = BitConverter.ToUInt32(bytes, (int)secondNode + 4);
            Assert.Equal(linkDuplicatePalettes,
                Pcx.ReadPaletteOnly(bytes.AsSpan((int)secondNode + 32, (int)secondLength)) is null);

            var saved = SffFile.Open(tmp);
            Assert.Equal(linkDuplicatePalettes,
                ReferenceEquals(saved.Sprites[0].Palette, saved.Sprites[1].Palette));
            Assert.NotSame(saved.Sprites[1].Palette, saved.Sprites[2].Palette);
            Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker097UsePreviousPaletteModeChainsSubheaderFlags()
    {
        static Palette Palette(byte red) => new()
        {
            Colors = Enumerable.Range(0, 256).Select(i => new Rgba(red, (byte)i, 0, 255)).ToArray(),
        };

        var first = Palette(10);
        var second = Palette(90);
        var fourth = Palette(170);
        var f = new SffFile { Version = SffVersions.V1, Palettes = [first, second, fourth] };
        f.Sprites.Add(new Sprite { Group = 1, Index = 0, Width = 1, Height = 1, Palette = first, Indexed = [1] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 1, Width = 1, Height = 1, Palette = second, Indexed = [2] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 2, Width = 1, Height = 1, Palette = first, Indexed = [1] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 3, Width = 1, Height = 1, Palette = fourth, Indexed = [3] });

        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            Assert.True(SprMakerOptions.SupportsUsePreviousPalette);
            var options = new SprMakerOptions { UsePreviousPalette = true, LinkDuplicateSprites = true };
            Assert.Empty(options.Unsupported());
            var savedReport = SprMakerSave.Save(f, tmp, options);
            Assert.Equal(1, savedReport.Linked);
            Assert.Equal([
                new SprMakerEvent(SprMakerEventKind.SpriteLinked, 1, 2, 1, 0),
                new SprMakerEvent(SprMakerEventKind.PaletteReused, 1, 1, 1, 0),
                new SprMakerEvent(SprMakerEventKind.PaletteReused, 1, 3, 1, 1),
            ], savedReport.Events);

            var bytes = File.ReadAllBytes(tmp);
            Assert.Equal((byte)1, bytes[32]);
            uint firstNode = BitConverter.ToUInt32(bytes, 24);
            uint secondNode = BitConverter.ToUInt32(bytes, (int)firstNode);
            uint thirdNode = BitConverter.ToUInt32(bytes, (int)secondNode);
            uint fourthNode = BitConverter.ToUInt32(bytes, (int)thirdNode);
            Assert.Equal((byte)0, bytes[(int)firstNode + 18]);
            Assert.Equal((byte)1, bytes[(int)secondNode + 18]);
            Assert.Equal(0u, BitConverter.ToUInt32(bytes, (int)thirdNode + 4));
            Assert.Equal((byte)1, bytes[(int)thirdNode + 18]);
            Assert.Equal((byte)1, bytes[(int)fourthNode + 18]);
            uint secondLength = BitConverter.ToUInt32(bytes, (int)secondNode + 4);
            uint fourthLength = BitConverter.ToUInt32(bytes, (int)fourthNode + 4);
            Assert.Null(Pcx.ReadPaletteOnly(bytes.AsSpan((int)secondNode + 32, (int)secondLength)));
            Assert.Null(Pcx.ReadPaletteOnly(bytes.AsSpan((int)fourthNode + 32, (int)fourthLength)));

            var saved = SffFile.Open(tmp);
            Assert.Same(saved.Sprites[0].Palette, saved.Sprites[1].Palette);
            Assert.Same(saved.Sprites[0], saved.Sprites[2].SharesWith);
            Assert.Same(saved.Sprites[0].Palette, saved.Sprites[3].Palette);
            Assert.NotSame(f.Sprites[0].Palette, f.Sprites[1].Palette);
            Assert.Null(f.Sprites[2].SharesWith);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker097DoesNotInferSamePaletteForDuplicateLinks()
    {
        static Rgba[] Colors(byte alpha) => Enumerable.Range(0, 256)
            .Select(i => new Rgba((byte)i, (byte)(255 - i), (byte)(i ^ 0x5a), alpha)).ToArray();

        var first = new Palette { Colors = Colors(255) };
        var sameRgb = new Palette { Colors = Colors(0) };
        var f = new SffFile { Version = SffVersions.V1, Palettes = [first, sameRgb] };
        f.Sprites.Add(new Sprite { Group = 1, Index = 0, Width = 1, Height = 1, Palette = first, Indexed = [7] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 1, Width = 1, Height = 1, Palette = sameRgb, Indexed = [9] });
        f.Sprites.Add(new Sprite { Group = 1, Index = 2, Width = 1, Height = 1, Palette = first, Indexed = [7] });

        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            SprMakerSave.Save(f, tmp, new SprMakerOptions
            {
                LinkDuplicateSprites = true,
                LinkDuplicatePalettes = true,
            });

            var bytes = File.ReadAllBytes(tmp);
            uint firstNode = BitConverter.ToUInt32(bytes, 24);
            uint secondNode = BitConverter.ToUInt32(bytes, (int)firstNode);
            uint thirdNode = BitConverter.ToUInt32(bytes, (int)secondNode);
            Assert.Equal(0u, BitConverter.ToUInt32(bytes, (int)thirdNode + 4));
            Assert.Equal((byte)0, bytes[(int)thirdNode + 18]);

            var saved = SffFile.Open(tmp);
            Assert.Same(saved.Sprites[0], saved.Sprites[2].SharesWith);
        }
        finally { File.Delete(tmp); }
    }

    // Sprite nodes as written: (format, depth, data length).
    private static List<(int Fmt, int Depth, uint Len)> Nodes(string path)
    {
        var b = File.ReadAllBytes(path);
        uint spr = BitConverter.ToUInt32(b, 36), n = BitConverter.ToUInt32(b, 40);
        var list = new List<(int, int, uint)>();
        for (int i = 0; i < n; i++)
        {
            int o = (int)spr + i * 28;
            list.Add((b[o + 14], b[o + 15], BitConverter.ToUInt32(b, o + 20)));
        }
        return list;
    }

    // As SprMaker 2.01 does (working/rawtest): a 32-color palette makes a
    // 5-bit sprite, anything else 8-bit, and every indexed sprite is encoded
    // afresh; raw data is exactly width*height bytes.
    [Theory]
    [InlineData(SprMaker2Compress5.Lz5, SprMaker2Compress8.Rle8, 4, 2)]
    [InlineData(SprMaker2Compress5.Rle5, SprMaker2Compress8.Rle8, 3, 2)]
    [InlineData(SprMaker2Compress5.None, SprMaker2Compress8.None, 0, 0)]
    public void SprMaker2EncodesByPaletteSize(SprMaker2Compress5 c5, SprMaker2Compress8 c8, int fmt5, int fmt8)
    {
        var f = Sample(SffVersions.V2_1);
        var pal256 = new Palette { Group = 1, Index = 3, Colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        f.Palettes.Add(pal256);
        f.Sprites[0].Palette = pal256;
        var pal32 = new Palette { Group = 1, Index = 2, Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        f.Palettes.Add(pal32);
        f.Sprites[1].Palette = pal32; // same low pixels as sprite 0, but a 32-color palette
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            SprMakerSave.Save(f, tmp, new SprMaker2Options { Compress5 = c5, Compress8 = c8 });
            var nodes = Nodes(tmp);
            Assert.Equal((fmt8, 8), (nodes[0].Fmt, nodes[0].Depth)); // 256-color palette: 8-bit, even with pixels below 32
            Assert.Equal((fmt5, 5), (nodes[1].Fmt, nodes[1].Depth));
            if (fmt8 == 0) Assert.All(nodes, n => Assert.Equal(16u, n.Len)); // 4x4, no length prefix

            var r = SffFile.Open(tmp);
            Assert.Equal(f.Sprites[0].Indexed, r.Sprites[0].Indexed);
            Assert.Equal(f.Sprites[1].Indexed, r.Sprites[1].Indexed);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker2NoneReencodesTrueColorAsRawRgb24()
    {
        byte[] source = [30, 20, 10, 0, 60, 50, 40, 128];
        var f = new SffFile { Version = SffVersions.V2_1 };
        f.Sprites.Add(new Sprite
        {
            Group = 1, Index = 1, Width = 2, Height = 1, Direct = source.ToArray(),
            SrcFormat = SpriteFormat.Raw, SrcDepth = 32, SourceData = [1, 2, 3, 4, 5, 6, 7, 8],
        });
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            Assert.True(SprMaker2Options.Supports(SprMaker2Compress24.None));
            Assert.Empty(new SprMaker2Options { Compress24 = SprMaker2Compress24.None }.Unsupported());
            SprMakerSave.Save(f, tmp, new SprMaker2Options { Compress24 = SprMaker2Compress24.None });

            var node = Nodes(tmp)[0];
            Assert.Equal((0, 24, 6u), (node.Fmt, node.Depth, node.Len));
            var saved = SffFile.Open(tmp).Sprites[0];
            Assert.Equal(SpriteFormat.Raw, saved.SrcFormat);
            Assert.Equal((byte)24, saved.SrcDepth);
            Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, saved.Direct);
            Assert.Equal(source, f.Sprites[0].Direct);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker2Png24ReencodesTrueColorAndDropsAlpha()
    {
        byte[] source = [30, 20, 10, 0, 60, 50, 40, 128];
        var f = new SffFile { Version = SffVersions.V2_1 };
        f.Sprites.Add(new Sprite
        {
            Group = 1, Index = 1, Width = 2, Height = 1, Direct = source.ToArray(),
            SrcFormat = SpriteFormat.Raw, SrcDepth = 32, SourceData = [1, 2, 3, 4, 5, 6, 7, 8],
        });
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            Assert.True(SprMaker2Options.Supports(SprMaker2Compress24.Png24));
            Assert.Empty(new SprMaker2Options { Compress24 = SprMaker2Compress24.Png24 }.Unsupported());
            SprMakerSave.Save(f, tmp, new SprMaker2Options { Compress24 = SprMaker2Compress24.Png24 });

            var node = Nodes(tmp)[0];
            Assert.Equal((11, 24), (node.Fmt, node.Depth));
            var saved = SffFile.Open(tmp).Sprites[0];
            Assert.Equal(SpriteFormat.Png24, saved.SrcFormat);
            Assert.Equal((byte)24, saved.SrcDepth);
            Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, saved.Direct);
            Assert.Equal(source, f.Sprites[0].Direct);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker2Png32ReencodesTrueColorAndKeepsAlpha()
    {
        byte[] source = [30, 20, 10, 128, 60, 50, 40, 255];
        var f = new SffFile { Version = SffVersions.V2_1 };
        f.Sprites.Add(new Sprite
        {
            Group = 1, Index = 1, Width = 2, Height = 1, Direct = source.ToArray(),
            SrcFormat = SpriteFormat.Raw, SrcDepth = 24, SourceData = [10, 20, 30, 40, 50, 60],
        });
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            SprMakerSave.Save(f, tmp, new SprMaker2Options { Compress24 = SprMaker2Compress24.Png32 });

            var node = Nodes(tmp)[0];
            Assert.Equal((12, 32), (node.Fmt, node.Depth));
            var saved = SffFile.Open(tmp).Sprites[0];
            Assert.Equal(source, saved.Direct);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void NormalSaveChoosesSmallestLosslessTrueColorEncoding()
    {
        static byte[] Solid(int width, int height, byte b, byte g, byte r, byte a)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
            return pixels;
        }

        const int large = 64;
        var opaqueLarge = Solid(large, large, 20, 40, 60, 255);
        var alphaLarge = new byte[large * large * 4];
        for (int p = 0; p < large * large; p++)
        {
            int i = p * 4;
            alphaLarge[i] = 20;
            alphaLarge[i + 1] = 40;
            alphaLarge[i + 2] = 60;
            alphaLarge[i + 3] = p % 2 == 0 ? (byte)128 : (byte)255;
        }

        var pixels = new[]
        {
            Solid(1, 1, 20, 40, 60, 255),
            opaqueLarge,
            Solid(1, 1, 20, 40, 60, 128),
            alphaLarge,
        };
        var f = new SffFile { Version = SffVersions.V2_1 };
        for (int i = 0; i < pixels.Length; i++)
        {
            int side = i is 1 or 3 ? large : 1;
            f.Sprites.Add(new Sprite
            {
                Group = 1, Index = i + 1, Width = side, Height = side,
                Direct = pixels[i], PixelsDirty = true,
            });
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"sfftest-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var nodes = Nodes(tmp);
            Assert.Equal((0, 24, 3u), (nodes[0].Fmt, nodes[0].Depth, nodes[0].Len));
            Assert.Equal((11, 24), (nodes[1].Fmt, nodes[1].Depth));
            Assert.True(nodes[1].Len < large * large * 3);
            Assert.Equal((0, 32, 4u), (nodes[2].Fmt, nodes[2].Depth, nodes[2].Len));
            Assert.Equal((12, 32), (nodes[3].Fmt, nodes[3].Depth));
            Assert.True(nodes[3].Len < large * large * 4);

            var saved = SffFile.Open(tmp);
            for (int i = 0; i < pixels.Length; i++)
                Assert.Equal(pixels[i], saved.Sprites[i].Direct);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UnsupportedOptionsAreRefused()
    {
        Assert.True(SprMakerOptions.SupportsUsePreviousPalette);
        Assert.True(SprMaker2Options.SupportsDecompressOnLoad);
        Assert.True(SprMaker2Options.SupportsVeryVerbose);
        Assert.Empty(new SprMakerOptions().Unsupported());
        Assert.Empty(new SprMaker2Options { DecompressOnLoad = true }.Unsupported());
        Assert.Empty(new SprMaker2Options { VerboseMode = true }.Unsupported());
        Assert.Empty(new SprMaker2Options { VeryVerboseMode = true }.Unsupported());
    }

    [Fact]
    public void SprMaker2DecompressOnLoadPutsSpritesInTdata()
    {
        var f = Sample(SffVersions.V2_1);
        f.Sprites[1].SharesWith = f.Sprites[0]; // links carry no data of their own
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            SprMakerSave.Save(f, tmp, new SprMaker2Options { DecompressOnLoad = true });

            var b = File.ReadAllBytes(tmp);
            uint ldataStart = BitConverter.ToUInt32(b, 52);
            uint ldataLen = BitConverter.ToUInt32(b, 56);
            uint tdataStart = BitConverter.ToUInt32(b, 60);
            uint tdataLen = BitConverter.ToUInt32(b, 64);
            // Only the 8-color palette lives in ldata; both sprites share one payload in tdata.
            Assert.Equal(8u * 4u, ldataLen);
            Assert.Equal(ldataStart + ldataLen, tdataStart);
            Assert.True(tdataLen > 0);
            Assert.Equal((uint)b.Length, tdataStart + tdataLen);

            uint spr = BitConverter.ToUInt32(b, 36);
            int o0 = (int)spr;
            uint off0 = BitConverter.ToUInt32(b, o0 + 16);
            uint len0 = BitConverter.ToUInt32(b, o0 + 20);
            Assert.Equal((ushort)1, BitConverter.ToUInt16(b, o0 + 26)); // flag 1: decompress once at load
            Assert.Equal(tdataLen, len0);
            Assert.True(off0 + len0 <= tdataLen); // offset is relative to tdata, not the file

            int o1 = (int)spr + 28;
            Assert.Equal(0u, BitConverter.ToUInt32(b, o1 + 20)); // the link borrows its target's pixels
            Assert.Equal((ushort)0, BitConverter.ToUInt16(b, o1 + 26)); // and carries no flag of its own

            var r = SffFile.Open(tmp);
            Assert.Same(r.Sprites[0], r.Sprites[1].SharesWith);
            Assert.Equal(f.Sprites[0].Indexed, r.Sprites[0].Indexed);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FindDuplicatePalettesFindsIdenticalPalettes()
    {
        var f = Sample(SffVersions.V2_1);
        var pal2 = new Palette { Group = 1, Index = 2, Colors = f.Palettes[0].Colors.ToArray() };
        f.Palettes.Add(pal2);

        var sets = f.FindDuplicatePalettes();
        Assert.Single(sets);
        Assert.Equal(2, sets[0].Count);
        Assert.Same(f.Palettes[0], sets[0][0]);
        Assert.Same(pal2, sets[0][1]);
    }

    [Fact]
    public void FindDuplicatePalettesIgnoresMaskColorRgbWhenAlphaIsZero()
    {
        var colors = Enumerable.Range(0, 8).Select(i => new Rgba((byte)i, (byte)i, (byte)i, 255)).ToArray();
        colors[0] = colors[0] with { A = 0 };
        var shifted = colors.ToArray();
        shifted[0] = shifted[0] with { R = 99 };
        var pal1 = new Palette { Group = 1, Index = 1, Colors = colors };
        var pal2 = new Palette { Group = 1, Index = 2, Colors = shifted };
        var f = new SffFile { Version = SffVersions.V2_1, Palettes = [pal1, pal2] };

        var sets = f.FindDuplicatePalettes();
        Assert.Single(sets);
        Assert.Equal(2, sets[0].Count);
        Assert.Empty(f.FindDuplicatePalettes(ignoreTransparentMaskRgb: false));
    }

    [Fact]
    public void FindDuplicatePalettesComparesIndexZeroAlpha()
    {
        var colors = Enumerable.Range(0, 8).Select(i => new Rgba((byte)i, (byte)i, (byte)i, 255)).ToArray();
        var shifted = colors.ToArray();
        shifted[0] = shifted[0] with { A = 0 };
        var pal1 = new Palette { Group = 1, Index = 1, Colors = colors };
        var pal2 = new Palette { Group = 1, Index = 2, Colors = shifted };
        var f = new SffFile { Version = SffVersions.V2_1, Palettes = [pal1, pal2] };

        Assert.Empty(f.FindDuplicatePalettes());
    }

    [Fact]
    public void ShareDuplicatePalettesLinksByDefault()
    {
        var f = Sample(SffVersions.V2_1);
        var pal2 = new Palette { Group = 1, Index = 2, Colors = f.Palettes[0].Colors.ToArray() };
        f.Palettes.Add(pal2);

        var before = f.SnapshotPixelLinks();
        var sets = f.FindDuplicatePalettes();
        Assert.Equal(1, f.ShareDuplicatePalettes(sets));
        Assert.Same(f.Palettes[0], pal2.LinksTo);

        before.Restore();
        Assert.Null(pal2.LinksTo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatePaletteOperationsHandleForwardLinksAndUndo(bool discard)
    {
        var target = new Palette
        {
            Group = 1, Index = 2,
            Colors = Enumerable.Range(0, 8).Select(i => new Rgba((byte)i, (byte)i, (byte)i, 255)).ToArray(),
        };
        var first = new Palette { Group = 1, Index = 1, LinksTo = target };
        var sprite = new Sprite { Group = 0, Index = 0, Width = 1, Height = 1, Indexed = [0], Palette = target };
        var f = new SffFile { Version = SffVersions.V2_1, Palettes = [first, target], Sprites = [sprite] };
        var before = f.SnapshotPixelLinks();

        var sets = f.FindDuplicatePalettes();
        Assert.Equal(1, f.ShareDuplicatePalettes(sets, discard));
        Assert.Null(first.LinksTo);
        if (discard)
        {
            Assert.Single(f.Palettes);
            Assert.Same(first, sprite.Palette);
        }
        else
        {
            Assert.Equal(2, f.Palettes.Count);
            Assert.Same(first, target.LinksTo);
        }

        before.Restore();
        Assert.Equal(2, f.Palettes.Count);
        Assert.Same(target, first.LinksTo);
        Assert.Null(target.LinksTo);
        Assert.Same(target, sprite.Palette);
    }

    [Fact]
    public void ShareDuplicatePalettesDiscardRemovesAndRepointsSprites()
    {
        var f = Sample(SffVersions.V2_1);
        var pal2 = new Palette { Group = 1, Index = 2, Colors = f.Palettes[0].Colors.ToArray() };
        f.Palettes.Add(pal2);
        f.Sprites[1].Palette = pal2; // sprite 1 uses the duplicate

        var sets = f.FindDuplicatePalettes();
        Assert.Equal(1, f.ShareDuplicatePalettes(sets, discard: true));
        Assert.DoesNotContain(pal2, f.Palettes);
        Assert.Same(f.Palettes[0], f.Sprites[1].Palette);
    }

    [Fact]
    public void SprMaker2DetectsDuplicatePalettes()
    {
        var f = Sample(SffVersions.V2_1);
        var pal2 = new Palette { Group = 1, Index = 2, Colors = f.Palettes[0].Colors.ToArray() };
        f.Palettes.Add(pal2);
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            var report = SprMakerSave.Save(f, tmp, new SprMaker2Options { DetectDuplicatePalettes = true });
            Assert.Equal([new SprMakerEvent(SprMakerEventKind.PaletteLinked, 1, 2, 1, 1)], report.Events);
            Assert.Equal(1, report.Palettes);

            var r = SffFile.Open(tmp);
            Assert.Same(r.Palettes[0], r.Palettes[1].LinksTo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker2UsesExactPaletteComparison()
    {
        var f = Sample(SffVersions.V2_1);
        var colors = f.Palettes[0].Colors.ToArray();
        colors[0] = colors[0] with { A = 0 };
        f.Palettes[0].Colors = colors;
        var shifted = colors.ToArray();
        shifted[0] = shifted[0] with { R = 99 };
        f.Palettes.Add(new Palette { Group = 1, Index = 2, Colors = shifted });

        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            var report = SprMakerSave.Save(f, tmp, new SprMaker2Options { DetectDuplicatePalettes = true });
            Assert.Equal(0, report.Palettes);

            var r = SffFile.Open(tmp);
            Assert.Equal(2, r.Palettes.Count);
            Assert.All(r.Palettes, p => Assert.Null(p.LinksTo));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMaker2DiscardsDuplicatePalettes()
    {
        var f = Sample(SffVersions.V2_1);
        var pal2 = new Palette { Group = 1, Index = 2, Colors = f.Palettes[0].Colors.ToArray() };
        f.Palettes.Add(pal2);
        f.Sprites[1].Palette = pal2;
        var tmp = Path.Combine(Path.GetTempPath(), $"sprmaker-{Guid.NewGuid():N}.sff");
        try
        {
            var report = SprMakerSave.Save(f, tmp, new SprMaker2Options { DiscardDuplicatePalettes = true });
            Assert.Equal([new SprMakerEvent(SprMakerEventKind.PaletteDiscarded, 1, 2, 1, 1)], report.Events);
            Assert.Equal((1, 1), (report.Palettes, report.PalettesDiscarded));

            var r = SffFile.Open(tmp);
            Assert.Single(r.Palettes);
            Assert.Same(r.Palettes[0], r.Sprites[1].Palette);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SprMakerReportToTextMatchesToolsLines()
    {
        var events = new List<SprMakerEvent>
        {
            new(SprMakerEventKind.SpriteCropped, 0, 0, OldWidth: 4, OldHeight: 4, NewWidth: 2, NewHeight: 2),
            new(SprMakerEventKind.SpriteLinked, 0, 1, 0, 0),
            new(SprMakerEventKind.PaletteLinked, 1, 2, 1, 1),
            new(SprMakerEventKind.PaletteDiscarded, 1, 3, 1, 1),
            new(SprMakerEventKind.PaletteReused, 1, 4, 1, 1),
            new(SprMakerEventKind.CropFollower, 0, 1, 0, 0),
        };
        var text = new SprMakerReport(events).ToText(v2: true, verbose: true).ReplaceLineEndings("\n");
        Assert.Contains("Autocrop sprite (0,0) 4x4 to 2x2", text);
        Assert.Contains("Linking sprite (0,1): same as (0,0)", text);
        Assert.Contains("Linking palette (1,2): same as (1,1)", text);
        Assert.Contains("Discarding palette (1,3): same as (1,1)", text);
        Assert.Contains("1 duplicate sprite(s) removed", text);
        Assert.Contains("1 sprite(s) cropped", text);

        var old = new SprMakerReport(events).ToText(v2: false, verbose: true).ReplaceLineEndings("\n");
        Assert.Contains("(0,0) cropped 4,4 -> 2,2", old);
        Assert.Contains("Sprite (0,1) linked to (0,0)", old);
        Assert.Contains("Duplicate pal eliminated (1,4): same as (1,1)", old);
        Assert.Contains("Total number of unique images:", old);

        var summary = new SprMakerReport(events).ToText(v2: true, verbose: false).ReplaceLineEndings("\n");
        Assert.DoesNotContain("Autocrop", summary);
        Assert.DoesNotContain("Linking", summary);
        Assert.Contains("1 duplicate sprite(s) removed", summary);
    }

    [Fact]
    public void SprMakerReportVeryVerboseAddsPerSourceLines()
    {
        var events = new List<SprMakerEvent>
        {
            new(SprMakerEventKind.SpriteCropped, 0, 0, OldWidth: 4, OldHeight: 4, NewWidth: 2, NewHeight: 2),
        };
        var reads = new List<SprMakerRead> { new(0, 0, HasPalette: true), new(0, 1, HasPalette: false) };

        var very = new SprMakerReport(events, reads)
            .ToText(v2: true, verbose: true, veryVerbose: true).ReplaceLineEndings("\n");
        Assert.Contains("Auto pal for (0,0)", very);
        Assert.Contains("Auto pal for (0,1)", very);
        Assert.Contains("Reading palette from (0,0).", very);
        Assert.DoesNotContain("Reading palette from (0,1).", very);
        Assert.Contains("Autocrop sprite (0,0) 4x4 to 2x2", very);

        var plain = new SprMakerReport(events, reads)
            .ToText(v2: true, verbose: true).ReplaceLineEndings("\n");
        Assert.DoesNotContain("Auto pal", plain);

        // -V alone implies the verbose lines.
        var vOnly = new SprMakerReport(events, reads)
            .ToText(v2: true, verbose: false, veryVerbose: true).ReplaceLineEndings("\n");
        Assert.Contains("Auto pal for (0,0)", vOnly);
        Assert.Contains("Autocrop sprite (0,0) 4x4 to 2x2", vOnly);
    }

    [Fact]
    public void SupportsPaletteDedupeIsTrue()
    {
        Assert.True(SprMaker2Options.SupportsPaletteDedupe);
    }
}
