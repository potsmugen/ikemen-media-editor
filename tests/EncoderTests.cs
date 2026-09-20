using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// The LZ5 encoder has to satisfy a decoder written from a reconstructed spec,
/// so round-tripping is the only real proof it is correct.
/// </summary>
public class EncoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public void Lz5RoundTripsRandomData(int seed)
    {
        var rng = new Random(seed);
        var pix = new byte[9000];
        for (int i = 0; i < pix.Length;)
        {
            // Runs and repeats, which is what sprite data actually looks like.
            byte v = (byte)rng.Next(32);
            int run = rng.Next(1, 40);
            for (int k = 0; k < run && i < pix.Length; k++) pix[i++] = v;
        }

        var packed = Lz5.Encode(pix);
        var back = new byte[pix.Length];
        Lz5.Decode(packed, back);
        Assert.Equal(pix, back);
    }

    [Fact]
    public void Lz5HandlesLongRunsAndDistantMatches()
    {
        var pix = new byte[4096];
        for (int i = 0; i < 300; i++) pix[i] = 5;                  // a run past 263
        for (int i = 300; i < 2048; i++) pix[i] = (byte)(i % 32);
        for (int i = 2048; i < 4096; i++) pix[i] = pix[i - 2048];  // beyond the 1024 window

        var back = new byte[pix.Length];
        Lz5.Decode(Lz5.Encode(pix), back);
        Assert.Equal(pix, back);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public void Rle5RoundTripsSpriteLikeData(int seed)
    {
        var rng = new Random(seed);
        var pix = new byte[9000];
        for (int i = 0; i < pix.Length;)
        {
            byte v = (byte)rng.Next(32);
            int run = rng.Next(1, 40);
            for (int k = 0; k < run && i < pix.Length; k++) pix[i++] = v;
        }

        var back = new byte[pix.Length];
        Rle5.Decode(Rle5.Encode(pix), back);
        Assert.Equal(pix, back);
    }

    [Fact]
    public void Rle5RoundTripsLongRunsAndManyPackets()
    {
        var pix = new List<byte>();
        pix.AddRange(Enumerable.Repeat((byte)0, 300)); // a run past the 256 leading cap
        pix.AddRange(Enumerable.Repeat((byte)7, 300));
        pix.AddRange(Enumerable.Repeat((byte)9, 17));
        for (int i = 0; i < 400; i++) pix.Add((byte)(i % 2 == 0 ? 3 : 4)); // past 127 sub-packets
        pix.Add(5); // a color run that ends the packet with no sub-packets

        var data = pix.ToArray();
        var back = new byte[data.Length];
        Rle5.Decode(Rle5.Encode(data), back);
        Assert.Equal(data, back);
    }

    [Fact]
    public void Lz5IsRefusedForIndicesAboveThirtyOne()
    {
        Assert.True(Lz5.CanEncode([0, 5, 31]));
        Assert.False(Lz5.CanEncode([0, 5, 32]));
    }

    [Fact]
    public void Lz5BeatsRle8OnRepetitiveData()
    {
        var pix = new byte[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                pix[y * 64 + x] = (byte)((x / 4) % 32); // identical rows, so every row after the first is a match

        Assert.True(Lz5.Encode(pix).Length < Rle8.Encode(pix).Length);
    }

    [Fact]
    public void PcxRoundTrips()
    {
        var pal = new Rgba[256];
        for (int i = 0; i < 256; i++) pal[i] = new Rgba((byte)i, (byte)(255 - i), 128, 255);

        var pix = new byte[37 * 11];
        var rng = new Random(3);
        for (int i = 0; i < pix.Length; i++) pix[i] = (byte)rng.Next(256);

        var pcx = PcxWriter.Encode(pix, 37, 11, pal, includePalette: true);
        var img = Pcx.Decode(pcx);

        Assert.Equal(37, img.Width);
        Assert.Equal(11, img.Height);
        Assert.Equal(pix, img.Indices);
        Assert.NotNull(img.Palette);
        Assert.Equal(pal[200], img.Palette![200]);
    }
}
