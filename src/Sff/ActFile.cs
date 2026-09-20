namespace Sff;

/// <summary>
/// A .act palette file: 768 bytes of RGB triplets, no header.
///
/// The catch is the order. MUGEN stores index 255 first and index 0 last, the
/// reverse of how Photoshop writes the same format, and getting it backwards
/// inverts a palette without any error to show for it. It was settled by
/// looking at a real file: read in reverse, 153 of its 256 entries fall inside
/// ascending brightness ramps, which is how shading ramps are laid out; read
/// forwards, not one does. The Reversed flag is kept anyway so a file that
/// turns out to be Photoshop-ordered can still be opened.
/// </summary>
public static class ActFile
{
    public const int Size = 768;

    public static Palette Open(string path, bool reversed = true)
    {
        var pal = Read(File.ReadAllBytes(path), reversed);
        pal.SourcePath = path;
        pal.Group = 1;
        return pal;
    }

    public static Palette Read(byte[] data, bool reversed = true)
    {
        if (data.Length < Size)
            throw new InvalidDataException($"an .act is {Size} bytes; this one is {data.Length}");

        var colors = new Rgba[256];
        for (int i = 0; i < 256; i++)
        {
            int at = (reversed ? 255 - i : i) * 3;
            // Index 0 is the transparency mask, so it is stored with zero alpha
            // to match how palettes read out of an SFF behave.
            colors[i] = new Rgba(data[at], data[at + 1], data[at + 2], i == 0 ? (byte)0 : (byte)255);
        }
        return new Palette { Colors = colors, Source = PaletteSource.External };
    }

    public static byte[] Write(Palette palette, bool reversed = true)
    {
        var data = new byte[Size];
        for (int i = 0; i < 256; i++)
        {
            var c = i < palette.Colors.Length ? palette.Colors[i] : default;
            int at = (reversed ? 255 - i : i) * 3;
            data[at] = c.R;
            data[at + 1] = c.G;
            data[at + 2] = c.B;
        }
        return data;
    }

    public static void Save(Palette palette, string path, bool reversed = true) =>
        File.WriteAllBytes(path, Write(palette, reversed));

    /// <summary>
    /// Guesses the ordering by counting entries that sit inside an ascending
    /// brightness ramp, which is what a hand-authored palette looks like. This
    /// is the check that settled the convention in the first place.
    /// </summary>
    public static bool LooksReversed(byte[] data)
    {
        if (data.Length < Size) return true;
        return RampScore(Read(data, reversed: true)) >= RampScore(Read(data, reversed: false));
    }

    private static int RampScore(Palette p)
    {
        int total = 0, run = 1;
        for (int i = 1; i < p.Colors.Length; i++)
        {
            var (a, b) = (p.Colors[i - 1], p.Colors[i]);
            bool blank = (a.R | a.G | a.B) == 0 || (b.R | b.G | b.B) == 0;
            if (!blank && b.R + b.G + b.B > a.R + a.G + a.B) run++;
            else { if (run >= 3) total += run; run = 1; }
        }
        return run >= 3 ? total + run : total;
    }
}
