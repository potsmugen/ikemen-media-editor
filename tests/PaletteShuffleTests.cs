using Editor;
using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class PaletteShuffleTests
{
    // An evenly tinted gray ramp whose dark end has a larger share of the sRGB maximum used to split into two groups
    [Fact]
    public void TintedGrayRampStaysOneGroup()
    {
        var source = new Dictionary<int, Rgba>
        {
            [1] = new(0xA0, 0xA4, 0xA8, 255),
            [2] = new(0x78, 0x7C, 0x80, 255),
            [3] = new(0x58, 0x5C, 0x60, 255),
            [4] = new(0x40, 0x44, 0x48, 255),
            [5] = new(0x30, 0x34, 0x38, 255),
        };
        MainWindow.ShuffleColors(source, new Random(1), 25, 30, 30, 35, 35, 30, 90, 50, 0, 0, out int groups);
        Assert.Equal(1, groups);
    }
}
