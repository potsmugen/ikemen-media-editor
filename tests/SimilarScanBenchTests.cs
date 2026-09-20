using System.Diagnostics;
using Sff;
using Xunit;
using Xunit.Abstractions;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Find Similar Sprites compares the current sprite with the whole file, so its cost
/// matters: it runs synchronously on the UI thread. This measures that, on synthetic
/// worst-case files — every sprite random, so nothing matches and no early exit fires.
///
/// Two things it pins down, both of which shaped the dialog's range:
///
/// - Cost rises as the percentage falls: low thresholds need more visible positions
///   to rule a sprite out, so they are the slow ones, not the quick ones.
///
/// - The score measures matching visible positions against all visible positions in
///   either drawing. Two solid blocks of the same color, 10x10 against 100x100, score
///   1% because the larger drawing has 9,900 additional visible positions.
///
/// Timings are printed, not asserted: they vary by machine, and a threshold-based
/// test on wall-clock time would only be flaky. The test fails only if a scan stops
/// completing at all.
/// </summary>
public class SimilarScanBenchTests(ITestOutputHelper output)
{
    /// <summary>Percentages worth timing: cheap, the default, and the slow ends.</summary>
    private static readonly int[] Percents = [100, 90, 50, 25, 10, 1];

    private static SffFile Synthetic(int count, int size, int seed = 12345)
    {
        var rand = new Random(seed);
        var palette = new Palette
        {
            Colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray(),
        };
        var f = new SffFile { Palettes = [palette] };
        for (int s = 0; s < count; s++)
        {
            // No index 0, so nothing is mask color and every sprite stays fully sized.
            var pixels = new byte[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)rand.Next(1, 256);
            f.Sprites.Add(new Sprite
            {
                Group = 1, Index = s, Width = size, Height = size, Palette = palette,
                Indexed = pixels, PixelsDirty = true,
            });
        }
        return f;
    }

    [Theory]
    [InlineData(400, 100)]
    [InlineData(1600, 100)]
    [InlineData(1600, 200)]
    public void ScanCostIsReasonableAtEveryThreshold(int count, int size)
    {
        var f = Synthetic(count, size);
        var reference = f.Sprites[count / 2];

        foreach (var percent in Percents)
        {
            var sw = Stopwatch.StartNew();
            var matches = f.FindSimilarSprites(reference, percent);
            sw.Stop();
            // Nothing in a random file resembles anything else.
            Assert.Empty(matches);
            output.WriteLine($"{count,4} sprites @ {size}px, {percent,3}%: {sw.ElapsedMilliseconds,6} ms");
        }
    }

    [Fact]
    public void SizeRatioCapsTheScore()
    {
        var palette = new Palette
        {
            Colors = Enumerable.Range(0, 256).Select(i => new Rgba((byte)i, 0, 0, 255)).ToArray(),
        };
        var f = new SffFile { Palettes = [palette] };
        f.Sprites.Add(new Sprite
        {
            Group = 1, Index = 0, Width = 10, Height = 10, Palette = palette,
            Indexed = Enumerable.Repeat((byte)7, 100).ToArray(), PixelsDirty = true,
        });
        f.Sprites.Add(new Sprite
        {
            Group = 1, Index = 1, Width = 100, Height = 100, Palette = palette,
            Indexed = Enumerable.Repeat((byte)7, 10_000).ToArray(), PixelsDirty = true,
        });

        // Both are a solid block of the same index: as alike as two sprites can be.
        var matches = f.FindSimilarSprites(f.Sprites[0], percent: 0);
        Assert.Single(matches);
        Assert.Equal(1, matches[0].Percent);
    }
}
