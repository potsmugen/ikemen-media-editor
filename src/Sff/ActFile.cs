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
///
/// Photoshop and Aseprite append 4 bytes: a big-endian color count and a transparent
/// index, and write index 0 first. Ikemen GO reads only the first 768 bytes.
/// </summary>
public static class ActFile
{
    public const int Size = 768;
    public const int TrailerSize = 4;

    /// <summary>The color count after the 768 bytes (1 to 256), or null when there's no valid one.</summary>
    public static int? TrailerCount(byte[] data)
    {
        if (data.Length < Size + 2) return null;
        int count = data[Size] << 8 | data[Size + 1];
        return count is >= 1 and <= 256 ? count : null;
    }

    /// <summary>The transparent-index field after the count, when the file has the full 4-byte trailer.</summary>
    public static ushort? TrailerTransparent(byte[] data) =>
        data.Length >= Size + TrailerSize && TrailerCount(data) is not null
            ? (ushort)(data[Size + 2] << 8 | data[Size + 3]) : null;

    public static Palette Open(string path, bool reversed = true)
    {
        var pal = Read(File.ReadAllBytes(path), reversed, countTrailer: false); // as Ikemen GO reads it
        pal.SourcePath = path;
        pal.Group = 1;
        return pal;
    }

    /// <summary>countTrailer: keep only the trailer's color count, when the file has one.</summary>
    public static Palette Read(byte[] data, bool reversed = true, bool countTrailer = true)
    {
        if (data.Length < Size)
            throw new InvalidDataException($"an .act is {Size} bytes; this one is {data.Length}");

        var colors = new Rgba[(countTrailer ? TrailerCount(data) : null) ?? 256];
        for (int i = 0; i < colors.Length; i++)
        {
            int at = (reversed ? 255 - i : i) * 3;
            // Index 0 is the transparency mask, so it is stored with zero alpha
            // to match how palettes read out of an SFF behave.
            colors[i] = new Rgba(data[at], data[at + 1], data[at + 2], i == 0 ? (byte)0 : (byte)255);
        }
        return new Palette { Colors = colors, Source = PaletteSource.External };
    }

    /// <summary>transparent non-null appends the trailer: the color count (at most 256) and that index.</summary>
    public static byte[] Write(Palette palette, bool reversed = true, ushort? transparent = null)
    {
        var data = new byte[transparent is null ? Size : Size + TrailerSize];
        for (int i = 0; i < 256; i++)
        {
            var c = i < palette.Colors.Length ? palette.Colors[i] : default;
            int at = (reversed ? 255 - i : i) * 3;
            data[at] = c.R;
            data[at + 1] = c.G;
            data[at + 2] = c.B;
        }
        if (transparent is { } t)
        {
            int count = Math.Clamp(palette.Colors.Length, 1, 256);
            (data[Size], data[Size + 1]) = ((byte)(count >> 8), (byte)count);
            (data[Size + 2], data[Size + 3]) = ((byte)(t >> 8), (byte)t);
        }
        return data;
    }

    public static void Save(Palette palette, string path, bool reversed = true, ushort? transparent = null) =>
        File.WriteAllBytes(path, Write(palette, reversed, transparent));

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
