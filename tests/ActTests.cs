using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// The only real risk with .act is the index order, since reading it backwards
/// produces a valid-looking but inverted palette rather than an error.
/// </summary>
public class ActTests
{
    private static byte[] MakeAct(Func<int, (byte R, byte G, byte B)> color, bool reversed)
    {
        var data = new byte[ActFile.Size];
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = color(i);
            int at = (reversed ? 255 - i : i) * 3;
            data[at] = r; data[at + 1] = g; data[at + 2] = b;
        }
        return data;
    }

    [Fact]
    public void ReadsMugenOrderWithIndexZeroLast()
    {
        var data = MakeAct(i => ((byte)i, (byte)(255 - i), 7), reversed: true);
        var pal = ActFile.Read(data);

        Assert.Equal(new Rgba(0, 255, 7, 0), pal.Colors[0]);      // mask entry, alpha zeroed
        Assert.Equal(new Rgba(200, 55, 7, 255), pal.Colors[200]);
        Assert.Equal(PaletteSource.Act, pal.Source);
    }

    [Fact]
    public void RoundTripsThroughWrite()
    {
        var original = ActFile.Read(MakeAct(i => ((byte)(i * 3 % 256), (byte)i, (byte)(255 - i)), true));
        var back = ActFile.Read(ActFile.Write(original));

        for (int i = 1; i < 256; i++) // index 0 carries alpha, which .act cannot store
            Assert.Equal(original.Colors[i], back.Colors[i]);
    }

    [Fact]
    public void DetectsOrderFromShadingRamps()
    {
        // A palette of ascending ramps, which is how they are authored.
        (byte, byte, byte) Ramp(int i) => ((byte)(i % 32 * 8), (byte)(i % 32 * 8), (byte)(i % 32 * 8));

        Assert.True(ActFile.LooksReversed(MakeAct(Ramp, reversed: true)));
        Assert.False(ActFile.LooksReversed(MakeAct(Ramp, reversed: false)));
    }

    [Fact]
    public void RefusesAFileOfTheWrongSize()
    {
        Assert.Throws<InvalidDataException>(() => ActFile.Read(new byte[100]));
    }
}
