using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class CompressionTests
{
    [Fact]
    public void TruncatedSffPixelStreamsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Decoder.DecodePixels(
            new Sprite { Width = 2, Height = 1, SrcFormat = SpriteFormat.Raw, SrcDepth = 24 }, [1, 2, 3, 4, 5]));
        Assert.Throws<InvalidDataException>(() => Rle8.Decode([1], new byte[2]));
        Assert.Throws<InvalidDataException>(() => Rle5.Decode([0, 0], new byte[2]));
        Assert.Throws<InvalidDataException>(() => Lz5.Decode([0, 5], new byte[2]));
    }

    [Fact]
    public void CompressedSffLengthMustMatchSpriteDimensions()
    {
        var sprite = new Sprite { Width = 2, Height = 1, SrcFormat = SpriteFormat.Rle8, SrcDepth = 8 };
        Assert.Throws<InvalidDataException>(() => Decoder.DecodePixels(sprite, [1, 0, 0, 0, 1, 2]));
    }

    [Fact]
    public void Rle8_ExpandsRunsAndLiterals()
    {
        // 0x05 literal, a run of four 0x09, then 0x41 as a 1-long run because a
        // bare 0x41 would be read as a control byte.
        byte[] src = [0x05, 0x44, 0x09, 0x41, 0x41];
        byte[] want = [0x05, 0x09, 0x09, 0x09, 0x09, 0x41];
        var got = new byte[want.Length];
        Rle8.Decode(src, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Rle5_ExpandsColorRunThenPackedBytes()
    {
        // run_length 2 (three pixels), color bit set, data_length 2, then two
        // 3-bit-run/5-bit-color bytes: a run of two color 7, then color 31.
        byte[] src = [0x02, 0x82, 0x0c, (1 << 5) | 7, (0 << 5) | 31];
        byte[] want = [0x0c, 0x0c, 0x0c, 7, 7, 31];
        var got = new byte[want.Length];
        Rle5.Decode(src, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Rle5_ColorBitClearMeansIndexZero()
    {
        byte[] src = [0x03, 0x01, (2 << 5) | 9];
        byte[] want = [0, 0, 0, 0, 9, 9, 9];
        var got = new byte[want.Length];
        Rle5.Decode(src, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Rle5_ZeroSubCountWithColorMeansOneTwentyEight()
    {
        // SprMaker writes a full packet's 128 sub-packets as a zero count.
        // Color bit set, data_length 0, color 5, then 128 one-pixel packets.
        var src = new byte[3 + 128];
        src[0] = 0x00;
        src[1] = 0x80;
        src[2] = 0x05;
        for (int i = 0; i < 128; i++) src[3 + i] = 0x06;

        var want = new byte[129];
        want[0] = 5;
        for (int i = 1; i < want.Length; i++) want[i] = 6;

        var got = new byte[want.Length];
        Rle5.Decode(src, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Rle5_ColorPacketStopsAtThePixelCount()
    {
        // A two-pixel sprite whose only packet announces 128 sub-packets but
        // carries none: the leading run fills the sprite, so nothing is read.
        byte[] src = [0x01, 0x80, 0x05];
        var got = new byte[2];
        Rle5.Decode(src, got);
        Assert.Equal(new byte[] { 5, 5 }, got);
    }

    [Fact]
    public void Lz5_ControlByteZeroMeansAllRlePackets()
    {
        byte[] src = [0x00, (2 << 5) | 5, (1 << 5) | 12];
        byte[] want = [5, 5, 12];
        var got = new byte[want.Length];
        Lz5.Decode(src, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Lz5_ShortPacketCopiesFromDistance()
    {
        // Packet 0 (RLE) emits 5,5,5; packet 1 (LZ) copies four bytes from distance 3.
        byte[] src = [0x02, (3 << 5) | 5, 0x03, 0x02];
        byte[] want = [5, 5, 5, 5, 5, 5, 5];
        var got = new byte[want.Length];
        Lz5.Decode(src, got);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Lz5_RejectsDistanceBeforeStartOfOutput()
    {
        byte[] src = [0x01, 0x01, 0x09];
        Assert.Throws<InvalidDataException>(() => Lz5.Decode(src, new byte[4]));
    }
}
