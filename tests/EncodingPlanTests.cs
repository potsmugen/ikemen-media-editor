using System.Buffers.Binary;
using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>Destination formats on SFF v2 saves (SffWriter.Destination, PlanEncoding and the save's own encoding).</summary>
public class EncodingPlanTests
{
    private static Palette Pal(int colors) => new()
    {
        Group = 1, Index = 1,
        Colors = Enumerable.Range(0, colors).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray(),
    };

    private static SffFile OneSprite(SffVersion version, Palette pal, Sprite s)
    {
        var f = new SffFile { Version = version, Palettes = [pal] };
        f.Sprites.Add(s);
        return f;
    }

    private static Sprite Edited(Palette pal, byte[] px, int w = 64, int h = 64) =>
        new() { Width = w, Height = h, Palette = pal, Indexed = px, PixelsDirty = true };

    private static Sprite StoredAsRle8(Palette pal, byte[] px)
    {
        var body = Rle8.Encode(px);
        var data = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)px.Length);
        body.CopyTo(data, 4);
        return new() { Width = 64, Height = 64, Palette = pal, Indexed = px, SrcFormat = SpriteFormat.Rle8, SrcDepth = 8, SourceData = data };
    }

    private static Sprite EditedDirect(byte alpha) =>
        new() { Width = 8, Height = 8, Direct = Enumerable.Range(0, 8 * 8 * 4).Select(i => i % 4 == 3 ? alpha : (byte)(i * 7)).ToArray(), PixelsDirty = true };

    /// <summary>64x64 random indices below max.</summary>
    private static byte[] Noise(int max, int seed = 1)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, 64 * 64).Select(_ => (byte)rng.Next(max)).ToArray();
    }

    private static SffFile SaveAndReload(SffFile f, SffVersion version, EncodingPlan? plan = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"encplan-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, version, plan: plan);
            var r = SffFile.Open(tmp);
            Assert.Equal(version, r.Version);
            return r;
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData(256, true, SpriteFormat.Png8)]
    [InlineData(32, true, SpriteFormat.Png8)]
    [InlineData(256, false, SpriteFormat.Rle8)]
    [InlineData(32, false, SpriteFormat.Lz5)]
    [InlineData(16, false, SpriteFormat.Lz5)]
    public void EditedIndexedSpriteTakesItsDestination(int colors, bool png, SpriteFormat expected)
    {
        var version = png ? SffVersions.V2_1 : SffVersions.V2;
        var pal = Pal(colors);
        var px = Noise(colors);
        var s = SaveAndReload(OneSprite(version, pal, Edited(pal, px)), version).Sprites[0];
        Assert.Equal(expected, s.SrcFormat);
        Assert.Equal(px, s.Indexed);
    }

    [Fact]
    public void TinySpritesAreNeverRaw()
    {
        // Raw would be smallest for a 1x1 sprite; Ikemen GO refuses it below 8 bits.
        var pal = Pal(256);
        var s = SaveAndReload(OneSprite(SffVersions.V2, pal, Edited(pal, [7], 1, 1)), SffVersions.V2).Sprites[0];
        Assert.Equal(SpriteFormat.Rle8, s.SrcFormat);
        Assert.Equal(new byte[] { 7 }, s.Indexed);
    }

    [Theory]
    [InlineData(255, SpriteFormat.Png24)]
    [InlineData(128, SpriteFormat.Png32)]
    public void EditedTrueColorIsPng24WhenOpaqueElsePng32(byte alpha, SpriteFormat expected)
    {
        var f = new SffFile { Version = SffVersions.V2_1 };
        var sprite = EditedDirect(alpha);
        f.Sprites.Add(sprite);
        var s = SaveAndReload(f, SffVersions.V2_1).Sprites[0];
        Assert.Equal(expected, s.SrcFormat);
        Assert.Equal(sprite.Direct, s.Direct);
    }

    [Fact]
    public void UntouchedSpriteIsReencodedOnlyWithAPlan()
    {
        var pal = Pal(256);
        var px = Noise(256);
        var f = OneSprite(SffVersions.V2_1, pal, StoredAsRle8(pal, px));

        Assert.Equal(SpriteFormat.Rle8, SaveAndReload(f, SffVersions.V2_1).Sprites[0].SrcFormat); // bytes reused

        var changing = Assert.Single(SffWriter.ReencodedOnSave(f, SffVersions.V2_1));
        Assert.Equal(SpriteFormat.Png8, changing.To);
        Assert.Equal(1, SffWriter.EncodingCount(f, SffVersions.V2_1));
        var plan = SffWriter.PlanEncoding(f, SffVersions.V2_1);
        var r = Assert.Single(plan.Reencoded);
        Assert.Equal((SpriteFormat.Rle8, SpriteFormat.Png8), (r.From, r.To));
        Assert.Equal((long)r.FromBytes - r.ToBytes, plan.BytesSaved);
        Assert.Empty(plan.CheckFailures);

        var saved = SaveAndReload(f, SffVersions.V2_1, plan).Sprites[0];
        Assert.Equal(SpriteFormat.Png8, saved.SrcFormat);
        Assert.Equal(px, saved.Indexed);

        // The session takes on the written bytes, so the next save has nothing to re-encode.
        SffWriter.ApplyEncoding(plan);
        Assert.Equal(SpriteFormat.Png8, f.Sprites[0].SrcFormat);
        Assert.Empty(SffWriter.ReencodedOnSave(f, SffVersions.V2_1));
        Assert.Equal(0, SffWriter.EncodingCount(f, SffVersions.V2_1));
    }

    [Fact]
    public void SpriteInItsDestinationIsKept()
    {
        var pal = Pal(256);
        var f = OneSprite(SffVersions.V2, pal, StoredAsRle8(pal, Noise(256)));
        Assert.Empty(SffWriter.ReencodedOnSave(f, SffVersions.V2));
        Assert.Empty(SffWriter.PlanEncoding(f, SffVersions.V2).Reencoded);
    }

    [Fact]
    public void LinkedSetIsEncodedOnce()
    {
        var pal = Pal(256);
        var px = Noise(256);
        var owner = StoredAsRle8(pal, px);
        var f = OneSprite(SffVersions.V2_1, pal, owner);
        f.Sprites.Add(new Sprite { Index = 1, Palette = pal, SharesWith = owner });
        var plan = SffWriter.PlanEncoding(f, SffVersions.V2_1);
        Assert.Same(owner, Assert.Single(plan.Reencoded).Sprite);

        var r = SaveAndReload(f, SffVersions.V2_1, plan);
        Assert.Equal(px, r.Sprites[0].Indexed);
        Assert.Equal(px, r.Sprites[1].Indexed);
    }

    [Fact]
    public void PlanForAnotherVersionIsRefused()
    {
        var pal = Pal(256);
        var f = OneSprite(SffVersions.V2_1, pal, StoredAsRle8(pal, Noise(256)));
        var plan = SffWriter.PlanEncoding(f, SffVersions.V2_1);
        var tmp = Path.Combine(Path.GetTempPath(), $"encplan-{Guid.NewGuid():N}.sff");
        try { Assert.Throws<ArgumentException>(() => f.Save(tmp, SffVersions.V2, plan: plan)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void RealBackgroundGetsSmallerWithSamePixels()
    {
        var path = WorkingFiles.Find("AbaddonBG_bug.sff");
        if (path is null) return;

        var f = SffFile.Open(path);
        var plan = SffWriter.PlanEncoding(f, f.Version);
        Assert.Equal(14, plan.Reencoded.Count);
        Assert.Empty(plan.CheckFailures);

        var tmp = Path.Combine(Path.GetTempPath(), $"encplan-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, f.Version, plan: plan);
            Assert.True(new FileInfo(tmp).Length < new FileInfo(path).Length);
            var r = SffFile.Open(tmp);
            for (int i = 0; i < f.Sprites.Count; i++)
            {
                Assert.Equal(f.Sprites[i].Indexed, r.Sprites[i].Indexed);
                Assert.Equal(f.Sprites[i].Direct, r.Sprites[i].Direct);
            }
        }
        finally { File.Delete(tmp); }
    }
}
