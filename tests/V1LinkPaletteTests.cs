using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// SFF v1 link palettes on the sample files (features.md: the same-palette flag and links).
/// A flagged link uses the previous palette, an unflagged one its target's; the sprites_org
/// dust shows it. Needs working/sprites_org.sff and the KFM samples; skips without them.
/// </summary>
public class V1LinkPaletteTests
{
    private static SffFile? Open(string name) => WorkingFiles.Find(name) is { } path ? SffFile.Open(path) : null;

    private static Sprite Get(SffFile f, int group, int index) =>
        f.Find(group, index) ?? throw new InvalidOperationException($"sprite {group},{index} is missing");

    /// <summary>6001,7-11 follow 6001,6's palette and 6001,13-17 follow 6001,12's, not their gray targets'.</summary>
    private static void AssertDustUsesThePreviousPalettes(SffFile f)
    {
        foreach (var (carrier, links) in new[] { (6, 7..12), (12, 13..18) })
        {
            var palette = Get(f, 6001, carrier).Palette!;
            for (int i = links.Start.Value; i < links.End.Value; i++)
            {
                var link = Get(f, 6001, i);
                Assert.NotNull(link.SharesWith);
                Assert.Equal(palette.Colors, link.Palette!.Colors);
                Assert.NotEqual(link.SharesWith!.Palette!.Colors, link.Palette.Colors);
            }
        }
    }

    [Fact]
    public void SpritesOrgDustLinksUseThePreviousPalette()
    {
        if (Open("sprites_org.sff") is not { } f) return;
        AssertDustUsesThePreviousPalettes(f);
        Assert.True(Get(f, 6001, 7).V1LinkFlag);
    }

    [Fact]
    public void SpritesOrgDustLinksKeepTheirPalettesThroughAPlainV1Save()
    {
        if (Open("sprites_org.sff") is not { } f) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"sprites_org-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V1);
            var r = SffFile.Open(tmp);
            AssertDustUsesThePreviousPalettes(r);
            Assert.True(Get(r, 6001, 7).V1LinkFlag);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SpritesOrgDustLinksKeepTheirPalettesThroughAV2Save()
    {
        if (Open("sprites_org.sff") is not { } f) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"sprites_org-{Guid.NewGuid():N}.sff");
        try
        {
            f.Save(tmp, SffVersions.V2);
            AssertDustUsesThePreviousPalettes(SffFile.Open(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SpritesOrgHasNoLinksLosingTheirPalette()
    {
        if (Open("sprites_org.sff") is not { } f) return;
        Assert.Empty(SffWriter.V1LinksLosingPalette(f));
    }

    [Fact]
    public void SpritesOrgListsNoV1DuplicatePalettes()
    {
        if (Open("sprites_org.sff") is not { } f) return;
        // One palette has an identical copy elsewhere, but not right after it: v1 stores it again.
        Assert.Contains(f.Palettes, a => f.Palettes.Any(b => !ReferenceEquals(a, b) && a.Colors.AsSpan().SequenceEqual(b.Colors)));
        Assert.Empty(f.FindDuplicatePalettes());
    }

    [Theory]
    [InlineData("kfm-v1.sff")]
    [InlineData("kfm - old.sff")]
    [InlineData("kfm-2000.sff")]
    [InlineData("kfm-v2.sff")]
    public void KfmLinksUseTheirTargetsPalette(string name)
    {
        // In KFM a link's previous palette is its target's, so every link shows the target's.
        if (Open(name) is not { } f) return;
        var links = f.Sprites.Where(s => s.SharesWith is not null).ToList();
        Assert.NotEmpty(links);
        Assert.All(links, s => Assert.Same(s.SharesWith!.Palette, s.Palette));
    }
}
