using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class GifTests
{
    [Fact]
    public void EncodesHeaderFramesAndTrailer()
    {
        var red = new byte[3 * 2 * 4];
        for (int i = 0; i < 6; i++) { red[i * 4 + 2] = 255; red[i * 4 + 3] = 255; }
        var clear = new byte[3 * 2 * 4];
        var gif = Gif.Encode(3, 2, [new GifFrame(red, 5), new GifFrame(clear, 7)]);

        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        Assert.Equal(3, gif[6] | gif[7] << 8);
        Assert.Equal(2, gif[8] | gif[9] << 8);
        Assert.Equal(0x3B, gif[^1]);
        int controls = 0;
        for (int i = 0; i + 1 < gif.Length; i++)
            if (gif[i] == 0x21 && gif[i + 1] == 0xF9) controls++;
        Assert.Equal(2, controls);
    }

    [Fact]
    public void RejectsEmptyAnimations() =>
        Assert.Throws<ArgumentException>(() => Gif.Encode(1, 1, []));
}
