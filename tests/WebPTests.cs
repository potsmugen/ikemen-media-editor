using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// Animated WebP container. The editor encodes frames with Skia; here a stand-in encoder returns a still
/// WebP whose VP8L chunk records the frame size, so the tests check layout, cropping and chunk handling.
/// </summary>
public class WebPTests
{
    private static byte[] Chunk(string tag, byte[] data)
    {
        var b = new List<byte>(System.Text.Encoding.ASCII.GetBytes(tag));
        b.AddRange(BitConverter.GetBytes((uint)data.Length));
        b.AddRange(data);
        if ((data.Length & 1) != 0) b.Add(0);
        return [.. b];
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        var payload = new List<byte>("WEBP"u8.ToArray());
        foreach (var c in chunks) payload.AddRange(c);
        return [.. "RIFF"u8.ToArray(), .. BitConverter.GetBytes((uint)payload.Count), .. payload];
    }

    /// <summary>Records each call's pixels; the VP8L payload is width and height as 16-bit values.</summary>
    private sealed class FakeEncoder
    {
        public readonly List<(byte[] Bgra, int Width, int Height)> Calls = [];

        public byte[] Encode(byte[] bgra, int width, int height)
        {
            Calls.Add((bgra, width, height));
            return Riff(Chunk("VP8X", new byte[10]), Chunk("ICCP", [1, 2, 3]),
                Chunk("VP8L", [(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8)]));
        }
    }

    private static List<(string Tag, byte[] Data)> Chunks(byte[] data, int start, int end)
    {
        var list = new List<(string, byte[])>();
        for (int i = start; i + 8 <= end;)
        {
            int size = BitConverter.ToInt32(data, i + 4);
            list.Add((System.Text.Encoding.ASCII.GetString(data, i, 4), data[(i + 8)..(i + 8 + size)]));
            i += 8 + size + (size & 1);
        }
        return list;
    }

    private static int U24(byte[] d, int i) => d[i] | d[i + 1] << 8 | d[i + 2] << 16;

    private static byte[] Solid(int count, byte b, byte g, byte r, byte a)
    {
        var px = new byte[count * 4];
        for (int i = 0; i < count; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (b, g, r, a);
        return px;
    }

    [Fact]
    public void WritesAnimatedContainer()
    {
        var enc = new FakeEncoder();
        var frame = Solid(6, 10, 20, 30, 255);
        var data = WebP.Encode(3, 2, [new WebPFrame(frame, 100), new WebPFrame(frame, 50)], enc.Encode);

        Assert.Equal("RIFF"u8.ToArray(), data[..4]);
        Assert.Equal(data.Length - 8, BitConverter.ToInt32(data, 4));
        Assert.Equal("WEBP"u8.ToArray(), data[8..12]);
        var top = Chunks(data, 12, data.Length);
        Assert.Equal(new[] { "VP8X", "ANIM", "ANMF", "ANMF" }, top.Select(c => c.Tag));
        Assert.Equal(0x02, top[0].Data[0]); // animation, no alpha
        Assert.Equal(3, U24(top[0].Data, 4) + 1);
        Assert.Equal(2, U24(top[0].Data, 7) + 1);
        Assert.Equal(0, BitConverter.ToUInt16(top[1].Data, 4)); // loops forever

        var first = top[2].Data;
        Assert.Equal((0, 0, 3, 2, 100), (U24(first, 0), U24(first, 3), U24(first, 6) + 1, U24(first, 9) + 1, U24(first, 12)));
        Assert.Equal(0b10, first[15]);
        var inner = Chunks(first, 16, first.Length);
        Assert.Equal(new[] { "VP8L" }, inner.Select(c => c.Tag)); // VP8X and ICCP from the still are dropped
        Assert.Equal(50, U24(top[3].Data, 12));
    }

    [Fact]
    public void LaterFramesKeepOnlyTheChangedRectangle()
    {
        var enc = new FakeEncoder();
        var a = Solid(10 * 8, 1, 2, 3, 255);
        var b = (byte[])a.Clone();
        foreach (int i in new[] { 3 + 5 * 10, 6 + 6 * 10 }) b[i * 4] = 99; // x 3..6, y 5..6
        var data = WebP.Encode(10, 8, [new WebPFrame(a, 10), new WebPFrame(b, 10), new WebPFrame(b, 10)], enc.Encode);

        var frames = Chunks(data, 12, data.Length).Where(c => c.Tag == "ANMF").Select(c => c.Data).ToList();
        // Corner rounds down to even: x 2, y 4, through x 6 and y 6.
        Assert.Equal((1, 2, 5, 3), (U24(frames[1], 0), U24(frames[1], 3), U24(frames[1], 6) + 1, U24(frames[1], 9) + 1));
        Assert.Equal((0, 0, 1, 1), (U24(frames[2], 0), U24(frames[2], 3), U24(frames[2], 6) + 1, U24(frames[2], 9) + 1));
        Assert.Equal(new[] { (10, 8), (5, 3), (1, 1) }, enc.Calls.Select(c => (c.Width, c.Height)));
        Assert.Equal(99, enc.Calls[1].Bgra[((5 - 4) * 5 + (3 - 2)) * 4]);
    }

    [Fact]
    public void FlagsAlphaAndClearsFullyTransparentColor()
    {
        var enc = new FakeEncoder();
        byte[] px = [10, 20, 30, 0, 40, 50, 60, 128];
        var data = WebP.Encode(2, 1, [new WebPFrame(px, 10)], enc.Encode);

        Assert.Equal(0x12, data[20]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 40, 50, 60, 128 }, enc.Calls[0].Bgra);
    }

    [Fact]
    public void KeepsLossyImageChunksAndPadsOddSizes()
    {
        var still = Riff(Chunk("VP8X", new byte[10]), Chunk("ALPH", [7]), Chunk("VP8 ", [1, 2, 3]), Chunk("EXIF", [9, 9]));
        var kept = WebP.ImageChunks(still);
        Assert.Equal(Chunk("ALPH", [7]).Concat(Chunk("VP8 ", [1, 2, 3])).ToArray(), kept);
    }

    [Fact]
    public void RejectsBadInputAndEncoderOutput()
    {
        var enc = new FakeEncoder();
        Assert.Throws<ArgumentException>(() => WebP.Encode(1, 1, [], enc.Encode));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebP.Encode(WebP.MaxSide + 1, 1, [new WebPFrame(new byte[(WebP.MaxSide + 1) * 4], 10)], enc.Encode));
        Assert.Throws<InvalidDataException>(() => WebP.Encode(1, 1, [new WebPFrame(new byte[4], 10)], (_, _, _) => "nope"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => WebP.ImageChunks(Riff(Chunk("VP8X", new byte[10]))));
    }
}
