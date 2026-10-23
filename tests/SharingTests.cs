using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Pixel sharing is held as an object reference rather than a list position, so
/// that reordering or inserting sprites cannot silently repoint a link. These
/// tests pin that behavior down.
/// </summary>
public class SharingTests
{
    private static SffFile TwoSprites()
    {
        var pal = new Palette { Colors = Enumerable.Range(0, 32).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray() };
        var a = new Sprite { Group = 1, Index = 0, Width = 4, Height = 4, Palette = pal, Indexed = new byte[16], PixelsDirty = true };
        for (int i = 0; i < 16; i++) a.Indexed![i] = (byte)(i % 32);
        var b = new Sprite { Group = 2, Index = 0, Width = 4, Height = 4, Palette = pal, Indexed = new byte[16], PixelsDirty = true };

        var f = new SffFile { Palettes = [pal] };
        f.Sprites.Add(a);
        f.Sprites.Add(b);
        return f;
    }

    [Fact]
    public void SharingAdoptsTheTargetsPixels()
    {
        var f = TwoSprites();
        Assert.True(f.Share(f.Sprites[1], f.Sprites[0]));
        Assert.Same(f.Sprites[0].Indexed, f.Sprites[1].Indexed);
        Assert.Same(f.Sprites[0], f.Sprites[1].SharesWith);
    }

    [Fact]
    public void SharingRefusesLoops()
    {
        var f = TwoSprites();
        Assert.True(f.Share(f.Sprites[1], f.Sprites[0]));
        Assert.False(f.Share(f.Sprites[0], f.Sprites[1])); // would be circular
        Assert.False(f.Share(f.Sprites[0], f.Sprites[0])); // and so would itself
    }

    [Fact]
    public void UnsharingTakesAPrivateCopy()
    {
        var f = TwoSprites();
        f.Share(f.Sprites[1], f.Sprites[0]);
        f.Sprites[1].Unshare();

        Assert.Null(f.Sprites[1].SharesWith);
        Assert.NotSame(f.Sprites[0].Indexed, f.Sprites[1].Indexed);
        Assert.Equal(f.Sprites[0].Indexed, f.Sprites[1].Indexed); // same content, different array

        f.Sprites[1].Indexed![0] = 99;
        Assert.NotEqual(99, f.Sprites[0].Indexed![0]); // editing one no longer touches the other
    }

    [Fact]
    public void InsertingASpriteBeforeTheTargetKeepsTheLinkCorrect()
    {
        var f = TwoSprites();
        f.Share(f.Sprites[1], f.Sprites[0]);
        var target = f.Sprites[0];

        // Insert at the front, which shifts every position by one.
        f.Sprites.Insert(0, new Sprite
        {
            Group = 9, Index = 9, Width = 4, Height = 4,
            Palette = f.Palettes[0], Indexed = new byte[16], PixelsDirty = true,
        });

        var tmp = Path.Combine(Path.GetTempPath(), $"share-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var reloaded = SffFile.Open(tmp);
            var shared = reloaded.Sprites.Single(s => s.Group == 2);
            Assert.NotNull(shared.SharesWith);
            Assert.Equal(target.Group, shared.SharesWith!.Group);
            Assert.Equal(target.Index, shared.SharesWith.Index);
            Assert.Equal(target.Indexed, shared.Indexed);
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AForwardLinkIsSavedPointingBack(bool v1)
    {
        // Ikemen GO loads a link to a later sprite as blank, so the earliest sprite of a set stores the pixels.
        var f = TwoSprites();
        if (v1) f.Version = SffVersions.V1;
        var (owner, link) = (f.Sprites[0], f.Sprites[1]);
        f.Share(link, owner);
        f.Sprites.Reverse(); // the link now comes first
        var pixels = owner.Indexed!.ToArray();

        var tmp = Path.Combine(Path.GetTempPath(), $"fwd-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, v1 ? SffVersions.V1 : SffVersions.V2_1);
            var r = SffFile.Open(tmp);
            Assert.Null(r.Sprites[0].SharesWith);
            Assert.Same(r.Sprites[0], r.Sprites[1].SharesWith);
            Assert.Equal((2, 1), (r.Sprites[0].Group, r.Sprites[1].Group));
            Assert.Equal(pixels, r.Sprites[0].Indexed);
            Assert.Equal(pixels, r.Sprites[1].Indexed);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void AForwardLinkToReusedBytesKeepsThem()
    {
        // The owner's on-disk bytes move to the earlier sprite instead of being re-encoded from nothing.
        var f = TwoSprites();
        f.Share(f.Sprites[1], f.Sprites[0]);
        var a = Path.Combine(Path.GetTempPath(), $"fwd-a-{Guid.NewGuid():N}.sff");
        var b = Path.Combine(Path.GetTempPath(), $"fwd-b-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(a, SffVersions.V2_1);
            var r = SffFile.Open(a);
            var pixels = r.Sprites[0].Indexed!.ToArray();
            r.Sprites.Reverse();
            r.Save(b, SffVersions.V2_1);
            var r2 = SffFile.Open(b);
            Assert.Null(r2.Sprites[0].SharesWith);
            Assert.Same(r2.Sprites[0], r2.Sprites[1].SharesWith);
            Assert.Equal(pixels, r2.Sprites[0].Indexed);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void ForwardLinksFromLoadCountUntilTheOrderPointsThemBack()
    {
        var f = TwoSprites();
        var (owner, link) = (f.Sprites[0], f.Sprites[1]);
        f.Share(link, owner);
        f.Sprites.Reverse();
        f.ForwardLinksOnLoad = [link];
        Assert.Equal(new[] { link }, SffWriter.ForwardLinksTurnedOnSave(f));

        f.Sprites.Reverse(); // back behind its target: nothing to turn around
        Assert.Empty(SffWriter.ForwardLinksTurnedOnSave(f));
    }

    [Fact]
    public void V1PaletteCheckSeesTheLinkTheSaveCreates()
    {
        // T stores pixels with palette P; L links to T with its own Q, taken from X just before it.
        var p = new Palette { Colors = [new Rgba(0, 0, 0, 0), new Rgba(10, 10, 10, 255)] };
        var q = new Palette { Colors = [new Rgba(0, 0, 0, 0), new Rgba(20, 20, 20, 255)] };
        var t = Pixels(1, p, 1);
        var x = Pixels(2, q, 2);
        var l = Pixels(3, q, 1);
        var f = new SffFile { Version = SffVersions.V1, Palettes = [p, q], Sprites = [t, x, l] };
        f.Share(l, t);
        Assert.Empty(SffWriter.V1LinksLosingPalette(f.Sprites.ToList()));

        // L first: it stores the pixels with Q, and T becomes a link that can keep neither P nor Q.
        Assert.Equal(new[] { t }, SffWriter.V1LinksLosingPalette(new List<Sprite> { l, t, x }));
    }

    private static Sprite Pixels(int group, Palette pal, byte fill) =>
        new() { Group = group, Width = 4, Height = 4, Palette = pal, Indexed = Enumerable.Repeat(fill, 16).ToArray(), PixelsDirty = true };

    [Fact]
    public void ShareDuplicatesLinksLaterCopiesToTheFirst()
    {
        var f = TwoSprites();
        var pal = f.Palettes[0];
        f.Sprites.Add(Pixels(3, pal, 0));  // same as sprite 1 (all zeros)
        f.Sprites.Add(Pixels(4, pal, 7));  // unique
        var other = new Palette { Colors = pal.Colors };
        f.Sprites.Add(Pixels(5, other, 0)); // v2: palette doesn't matter

        var sets = f.FindDuplicateSprites();
        Assert.Single(sets);
        Assert.Equal(2, f.ShareDuplicateSprites(sets));
        Assert.Same(f.Sprites[1], f.Sprites[2].SharesWith);
        Assert.Same(f.Sprites[1], f.Sprites[4].SharesWith);
        Assert.Null(f.Sprites[3].SharesWith);
        Assert.Same(other, f.Sprites[4].Palette); // keeps its own palette
        Assert.Empty(f.FindDuplicateSprites());   // nothing left to do
    }

    [Fact]
    public void ShareDuplicatesInV1NeedsTheSamePaletteColors()
    {
        var f = TwoSprites();
        f.Version = SffVersions.V1;
        var pal = f.Palettes[0];
        f.Sprites.Add(Pixels(3, new Palette { Colors = (Rgba[])pal.Colors.Clone() }, 0)); // same colors, separate object
        var shifted = pal.Colors.Select(c => c with { R = (byte)(c.R + 1) }).ToArray();
        f.Sprites.Add(Pixels(4, new Palette { Colors = shifted }, 0));                    // different colors

        var sets = f.FindDuplicateSprites();
        Assert.Single(sets);
        Assert.Equal(1, f.ShareDuplicateSprites(sets));
        Assert.Same(f.Sprites[1], f.Sprites[2].SharesWith);
        Assert.Null(f.Sprites[3].SharesWith);
    }

    [Fact]
    public void ShareDuplicatesInV1AdoptsTheTargetsPaletteAndDropsUnusedOnes()
    {
        var f = TwoSprites();
        f.Version = SffVersions.V1;
        var pal = f.Palettes[0];
        var copy = new Palette { Colors = (Rgba[])pal.Colors.Clone() };
        var kept = new Palette { Colors = (Rgba[])pal.Colors.Clone() };
        f.Palettes.Add(copy);
        f.Palettes.Add(kept);
        f.Sprites.Add(Pixels(3, copy, 0));                                   // linked, its palette ends up unused
        f.Sprites.Add(Pixels(4, kept, 0));                                   // linked, but ...
        f.Sprites.Add(Pixels(5, kept, 9));                                   // ... this one still uses kept

        var before = f.SnapshotPixelLinks();
        f.ShareDuplicateSprites(f.FindDuplicateSprites());
        Assert.Same(pal, f.Sprites[2].Palette);
        Assert.Same(pal, f.Sprites[3].Palette);
        Assert.DoesNotContain(copy, f.Palettes);
        Assert.Contains(kept, f.Palettes);

        before.Restore();
        Assert.Same(copy, f.Sprites[2].Palette);
        Assert.Equal(new[] { pal, copy, kept }, f.Palettes);
    }

    [Fact]
    public void ShareDuplicatesFlipsAForwardLinkWithoutALoop()
    {
        var f = TwoSprites();
        Assert.True(f.Share(f.Sprites[0], f.Sprites[1])); // first borrows from the second
        f.Sprites[1].PixelsDirty = false;

        var sets = f.FindDuplicateSprites();
        Assert.Single(sets);
        f.ShareDuplicateSprites(sets);
        Assert.Null(f.Sprites[0].SharesWith);
        Assert.Same(f.Sprites[0], f.Sprites[1].SharesWith);
    }

    [Fact]
    public void SnapshotUndoesShareDuplicates()
    {
        var f = TwoSprites();
        f.Sprites.Add(Pixels(3, f.Palettes[0], 0));
        var before = f.SnapshotPixelLinks();
        var pixels = f.Sprites[2].Indexed;
        f.ShareDuplicateSprites(f.FindDuplicateSprites());
        before.Restore();
        Assert.Null(f.Sprites[2].SharesWith);
        Assert.Same(pixels, f.Sprites[2].Indexed);
        Assert.True(f.Sprites[2].PixelsDirty);
    }

    [Fact]
    public void MergePaletteRemovesSourceAndRedirectsSpriteAndPaletteUsers()
    {
        var target = new Palette { Group = 1, Index = 1, Colors = [new Rgba(1, 2, 3, 255)] };
        var source = new Palette { Group = 1, Index = 2, Colors = [new Rgba(4, 5, 6, 255)] };
        var dependent = new Palette { Group = 1, Index = 3, LinksTo = source };
        var directUser = Pixels(1, source, 0);
        var dependentUser = Pixels(2, dependent, 0);
        var f = new SffFile
        {
            Version = SffVersions.V2_1,
            Palettes = [target, source, dependent],
            Sprites = [directUser, dependentUser],
        };
        var before = f.SnapshotPixelLinks();

        Assert.Equal((1, 1), f.MergePalette(source, target));
        Assert.Equal(new[] { target, dependent }, f.Palettes);
        Assert.Same(target, directUser.Palette);
        Assert.Same(target, dependent.LinksTo);
        Assert.Same(dependent, dependentUser.Palette);

        var after = f.SnapshotPixelLinks();
        before.Restore();
        Assert.Equal(new[] { target, source, dependent }, f.Palettes);
        Assert.Same(source, directUser.Palette);
        Assert.Same(source, dependent.LinksTo);

        after.Restore();
        Assert.Equal(new[] { target, dependent }, f.Palettes);
        Assert.Same(target, directUser.Palette);
        Assert.Same(target, dependent.LinksTo);
    }

    [Fact]
    public void SharedDuplicatesSurviveASaveRoundTrip()
    {
        var f = TwoSprites();
        f.Sprites.Add(Pixels(3, f.Palettes[0], 0));
        f.ShareDuplicateSprites(f.FindDuplicateSprites());
        var tmp = Path.Combine(Path.GetTempPath(), $"dups-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp);
            var r = SffFile.Open(tmp);
            Assert.Equal(2, r.Sprites.Single(s => s.Group == 3).SharesWith!.Group);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ABorrowerAlwaysSeesItsTargetsCurrentPixels()
    {
        var f = TwoSprites();
        var (target, borrower) = (f.Sprites[0], f.Sprites[1]);
        f.Share(borrower, target);

        target.Indexed = new byte[] { 5, 5 };
        target.Width = 2;
        target.Height = 1;
        Assert.Same(target.Indexed, borrower.Indexed);
        Assert.Equal((2, 1), (borrower.Width, borrower.Height));

        borrower.Unshare();
        Assert.Equal(new byte[] { 5, 5 }, borrower.Indexed);
        Assert.NotSame(target.Indexed, borrower.Indexed);
        Assert.Equal((2, 1), (borrower.Width, borrower.Height));
    }

    [Fact]
    public void CropRefusesABorrowerAndCroppingTheTargetReachesIt()
    {
        var f = TwoSprites();
        var (target, borrower) = (f.Sprites[0], f.Sprites[1]);
        target.Indexed = new byte[16];
        target.Indexed[5] = 3; // one visible pixel at (1,1)
        f.Share(borrower, target);

        Assert.Throws<InvalidOperationException>(() => SpriteCrop.Crop(borrower));
        Assert.Same(target, borrower.SharesWith);

        Assert.True(SpriteCrop.Crop(target).Changed);
        Assert.Equal((1, 1), (borrower.Width, borrower.Height));
        Assert.Same(target, borrower.SharesWith);
    }
}
