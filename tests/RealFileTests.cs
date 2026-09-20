using System.Buffers.Binary;
using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Runs against a real character file when one is available. Drop an SFFv2 named
/// kfm-v2.sff in working/ to enable these; without it they skip, so the
/// suite stays green in a fresh clone.
/// </summary>
public class RealFileTests
{
    private static string? FindSample() => WorkingFiles.Find("kfm-v2.sff");

    [Fact]
    public void EverySpriteDecodes()
    {
        var path = FindSample();
        if (path is null) return;

        var f = SffFile.Open(path);
        Assert.NotEmpty(f.Sprites);
        foreach (var s in f.Sprites)
        {
            Assert.Null(s.DecodeError);
            Assert.True(s.HasPixels, $"sprite {s.Group},{s.Index} produced no pixels");
            if (s.Indexed is not null)
                Assert.Equal(s.Width * s.Height, s.Indexed.Length);
        }
    }

    [Fact]
    public void FiveBitSpritesStayWithinFiveBits()
    {
        var path = FindSample();
        if (path is null) return;

        foreach (var s in SffFile.Open(path).Sprites)
        {
            if (s.SrcFormat is not (SpriteFormat.Lz5 or SpriteFormat.Rle5) || s.Indexed is null) continue;
            foreach (var v in s.Indexed)
                Assert.True(v <= 31, $"sprite {s.Group},{s.Index} holds index {v} in a 5-bit sprite");
        }
    }

    /// <summary>
    /// A correct LZ5 decode mostly copies the pixel directly above, so
    /// back-reference distances should pile up at exactly the sprite width.
    /// This is the check that found the missing +1 on the long-packet distance:
    /// without it the distances sat one short of the width.
    /// </summary>
    [Fact]
    public void BackReferenceDistancesPeakAtSpriteWidth()
    {
        var path = FindSample();
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        var f = SffFile.Open(path);

        int atWidth = 0, total = 0;
        foreach (var s in f.Sprites)
        {
            if (s.SrcFormat != SpriteFormat.Lz5 || s.SharesWith is not null) continue;
            var payload = RawPayload(bytes, f, s);
            if (payload.Length <= 4) continue;

            var distances = new List<int>();
            Lz5.Decode(payload.AsSpan(4), new byte[s.Width * s.Height], distances);
            foreach (var d in distances)
            {
                total++;
                if (d == s.Width) atWidth++;
            }
        }

        Assert.True(total > 1000, $"expected a meaningful sample, saw {total} back-references");
        Assert.True(atWidth * 100 / total >= 25,
            $"only {atWidth * 100 / total}% of back-references copy the row above; the decoder is probably off by one");
    }

    /// <summary>Re-reads one sprite's compressed bytes straight out of the file.</summary>
    private static byte[] RawPayload(byte[] b, SffFile f, Sprite sprite)
    {
        uint sprOffset = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
        uint ldata = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(52));
        uint tdata = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(60));

        int i = f.Sprites.IndexOf(sprite);
        var n = b.AsSpan((int)sprOffset + i * 28, 28);
        int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(n[16..]);
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(n[20..]);
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(n[26..]);
        int start = (flags & 1) != 0 ? (int)tdata + off : (int)ldata + off;
        return b.AsSpan(start, len).ToArray();
    }
}
