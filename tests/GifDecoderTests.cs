using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class GifDecoderTests
{
    private static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = b; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = r; pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    [Fact]
    public void RoundTripsFramesThroughTheEncoder()
    {
        var red = Solid(4, 3, 255, 0, 0);
        var green = Solid(4, 3, 0, 255, 0);
        var encoded = Gif.Encode(4, 3, [new GifFrame(red, 5), new GifFrame(green, 7)]);

        var frames = GifDecoder.Decode(encoded, out int width, out int height);

        Assert.Equal(4, width);
        Assert.Equal(3, height);
        Assert.Equal(2, frames.Count);
        Assert.Equal(red, frames[0].Bgra);
        Assert.Equal(green, frames[1].Bgra);
        Assert.Equal(5, frames[0].DelayCs);
        Assert.Equal(7, frames[1].DelayCs);
    }

    [Fact]
    public void FrameCountAgreesWithDecoding() => Assert.Equal(2, GifImport.FrameCount(
        Gif.Encode(2, 2, [new GifFrame(Solid(2, 2, 255, 0, 0), 5), new GifFrame(Solid(2, 2, 0, 0, 255), 5)])));

    [Fact]
    public void RecognizesTheSignature()
    {
        Assert.True(GifDecoder.LooksLikeGif(Gif.Encode(1, 1, [new GifFrame(Solid(1, 1, 0, 0, 0), 1)])));
        Assert.False(GifDecoder.LooksLikeGif([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Throws<InvalidDataException>(() => GifDecoder.Decode([1, 2, 3], out _, out _));
    }

    [Fact]
    public void BuildsOneSpritePerFrameUsingTheGivenPlan()
    {
        var encoded = Gif.Encode(2, 2, [new GifFrame(Solid(2, 2, 255, 0, 0), 5), new GifFrame(Solid(2, 2, 0, 0, 255), 5)]);

        var sprites = GifImport.ToSprites(GifImport.DecodeFrames(encoded), ImportPlan.TrueColor32(), out var reports);

        Assert.Equal(2, sprites.Count);
        Assert.Equal(2, reports.Count);
        Assert.All(sprites, s =>
        {
            Assert.Equal(2, s.Width);
            Assert.Equal(2, s.Height);
            Assert.NotNull(s.Direct);
            Assert.Null(s.Indexed);
        });
        Assert.All(reports, r => Assert.True(r.TrueColor));
    }

    [Fact]
    public void PutsGifTransparencyOnColorIndexZero()
    {
        // Left pixel transparent, right pixel opaque: the transparent one must
        // land on the mask index. The sprite is built with a chosen palette
        // (no threshold involved) to prove the pinning is the GIF layer's doing
        // and not a side effect of the nearest-color threshold.
        RawGif.TableEntry[] table =
        [
            new(0, 0, 255), // 0: the transparent index
            new(0, 255, 0),
            new(255, 0, 0),
        ];
        var gif = RawGif.Build(2, 1, table,
            new RawGif.FrameSpec(0, 0, 2, 1, [0, 1], Transparent: 0));

        // A palette whose only non-mask entry is red, so nearest matching would
        // never pick index 0 for an opaque pixel on its own.
        var target = new Palette { Colors = [new(0, 0, 0, 0), new(255, 0, 0, 255)] };
        var sprite = GifImport.ToSprites(GifImport.DecodeFrames(gif),
            ImportPlan.Given(target, keepIndexes: false), out _)[0];

        Assert.NotNull(sprite.Indexed);
        Assert.Equal(Sprite.TransparentIndex, sprite.Indexed[0]);
        Assert.Equal(1, sprite.Indexed[1]);
    }
}

public class GifDuplicateTests
{
    private static GifImport.DecodedFrame Frame(byte r)
    {
        var pixels = new byte[2 * 4];
        for (int i = 0; i < 2; i++)
        {
            pixels[i * 4] = r; pixels[i * 4 + 3] = 255;
        }
        return new GifImport.DecodedFrame(2, 1, pixels, 5);
    }

    [Fact]
    public void DropsAFrameThatRepeatsTheOneBefore()
    {
        var frames = new[] { Frame(1), Frame(1), Frame(2) };

        var kept = GifImport.DropDuplicates(frames);

        Assert.Equal(2, kept.Count);
        Assert.Equal(1, kept[0].Bgra[0]);
        Assert.Equal(2, kept[1].Bgra[0]);
    }

    [Fact]
    public void DropsAFrameThatReturnsToAnEarlierImage()
    {
        var frames = new[] { Frame(1), Frame(2), Frame(3), Frame(2) };

        Assert.Equal(1, GifImport.DuplicateCount(frames));
        Assert.Equal(3, GifImport.DropDuplicates(frames).Count);
    }

    [Fact]
    public void HashCollisionDoesNotDropDistinctFrames()
    {
        var frames = new[] { Frame(1), Frame(2), Frame(1) };

        var kept = GifImport.DropDuplicates(frames, _ => 0);

        Assert.Equal(2, kept.Count);
        Assert.Equal(frames[0], kept[0]);
        Assert.Equal(frames[1], kept[1]);
    }

    [Fact]
    public void KeepsEveryDistinctFrame()
    {
        var frames = new[] { Frame(1), Frame(2), Frame(3) };

        Assert.Equal(0, GifImport.DuplicateCount(frames));
        Assert.Equal(3, GifImport.DropDuplicates(frames).Count);
    }

    [Fact]
    public void NeverDropsTheFirstFrame() =>
        Assert.Single(GifImport.DropDuplicates([Frame(9)]));
}

/// <summary>
/// Builds GIFs byte by byte so the decoder is checked against bytes it did not
/// produce itself. LZW here emits a clear code before every literal, which is
/// slow but legal and keeps the hand-built streams easy to read.
/// </summary>
internal static class RawGif
{
    internal readonly record struct TableEntry(byte R, byte G, byte B);

    /// <summary>Four colors, so an index can be told from any other by its color alone.</summary>
    public static readonly TableEntry[] FourColors =
        [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0)];

    /// <summary>The name FourColors gives an index.</summary>
    public static string ColorName(int index) => (index % 4) switch
    {
        0 => "red", 1 => "green", 2 => "blue", _ => "yellow",
    };

    /// <summary>Names the color at a pixel, so assertions read as colors.</summary>
    public static string ColorAt(byte[] bgra, int width, int x, int y)
    {
        int o = (y * width + x) * 4;
        return (bgra[o + 2], bgra[o + 1], bgra[o]) switch
        {
            (255, 0, 0) => "red",
            (0, 255, 0) => "green",
            (0, 0, 255) => "blue",
            (255, 255, 0) => "yellow",
            _ => $"rgb({bgra[o + 2]},{bgra[o + 1]},{bgra[o]})",
        };
    }

    /// <summary>Packs codes into an LZW stream, least-significant bit first, at the
    /// widths given. A stream the decoder has to read at exactly these widths.</summary>
    public static byte[] PackLzw(params (int Code, int Width)[] codes)
    {
        var bits = new List<int>();
        foreach (var (code, width) in codes)
            for (int i = 0; i < width; i++) bits.Add((code >> i) & 1);
        var outp = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i] != 0) outp[i / 8] |= (byte)(1 << (i % 8));
        return outp;
    }

    /// <param name="Disposal">0 keep, 1 keep, 2 restore to background.</param>
    /// <param name="Transparent">Color-table index to leave alone, or null for an opaque frame.</param>
    /// <param name="Interlaced">Store the rows in the four interlaced passes rather than in order.</param>
    /// <param name="Stream">A packed LZW stream to store as-is, instead of encoding Indices.</param>
    public readonly record struct FrameSpec(
        int X, int Y, int Width, int Height, byte[] Indices, int Disposal = 0, int? Transparent = null,
        bool Interlaced = false, byte[]? Stream = null);

    public static byte[] Build(int width, int height, TableEntry[] table, params FrameSpec[] frames)
    {
        // 4 table entries: the size field encodes 2^(N+1), and our decoder reads
        // 2 << (packed & 7).
        int sizeBits = 0;
        while (1 << (sizeBits + 1) < table.Length) sizeBits++;

        var outp = new List<byte>();
        outp.AddRange("GIF89a"u8.ToArray());
        outp.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8)]);
        outp.Add((byte)(0x80 | sizeBits << 4 | sizeBits));
        outp.Add(0); // background index
        outp.Add(0); // aspect ratio
        for (int i = 0; i < 1 << (sizeBits + 1); i++)
        {
            var e = i < table.Length ? table[i] : default;
            outp.AddRange([e.R, e.G, e.B]);
        }

        foreach (var f in frames)
        {
            outp.AddRange([0x21, 0xF9, 0x04]);
            outp.Add((byte)(f.Disposal << 2 | (f.Transparent is null ? 0 : 1)));
            outp.AddRange([0x00, 0x05]); // delay, hundredths
            outp.Add((byte)(f.Transparent ?? 0));
            outp.Add(0); // block terminator

            outp.Add(0x2C);
            outp.AddRange([(byte)f.X, (byte)(f.X >> 8), (byte)f.Y, (byte)(f.Y >> 8)]);
            outp.AddRange([(byte)f.Width, (byte)(f.Width >> 8), (byte)f.Height, (byte)(f.Height >> 8)]);
            outp.Add((byte)(f.Interlaced ? 0x40 : 0x00)); // no local color table

            outp.Add(2); // minimum code size
            var blocks = f.Stream is { } stream ? Split(stream) : LzwLiteral(Stored(f));
            foreach (var block in blocks) outp.AddRange([(byte)block.Length]);
            foreach (var block in blocks) outp.AddRange(block);
            outp.Add(0x00); // end of image data
        }

        outp.Add(0x3B); // trailer
        return [.. outp];
    }

    /// <summary>Splits a stream into the 255-byte blocks the format requires.</summary>
    private static List<byte[]> Split(byte[] data)
    {
        var blocks = new List<byte[]>();
        for (int i = 0; i < Math.Max(data.Length, 1); i += 255)
            blocks.Add(data[i..Math.Min(i + 255, data.Length)]);
        return blocks;
    }

    /// <summary>A frame's indexes in the order the file stores them: an interlaced
    /// frame holds its rows out of order, in the four passes.</summary>
    private static byte[] Stored(FrameSpec f)
    {
        if (!f.Interlaced) return f.Indices;
        var outp = new byte[f.Indices.Length];
        int at = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
            for (int y = start; y < f.Height; y += step)
                for (int x = 0; x < f.Width; x++)
                    outp[at++] = f.Indices[y * f.Width + x];
        return outp;
    }

    private static List<byte[]> LzwLiteral(byte[] indices)
    {
        int clear = 4, end = 5, codeSize = 3; // min code size 2
        int buf = 0, bits = 0;
        var bytes = new List<byte>();
        var blocks = new List<byte[]>();

        void Emit(int code)
        {
            buf |= code << bits;
            bits += codeSize;
            while (bits >= 8) { bytes.Add((byte)buf); buf >>= 8; bits -= 8; }
        }

        foreach (var value in indices)
        {
            Emit(clear);   // reset the table, so codes never grow past 3 bits
            Emit(value);
        }
        Emit(end);
        if (bits > 0) bytes.Add((byte)buf);

        for (int i = 0; i < bytes.Count; i += 255)
            blocks.Add(bytes.GetRange(i, Math.Min(255, bytes.Count - i)).ToArray());
        return blocks;
    }
}

public class GifCompositingTests
{
    private static readonly RawGif.TableEntry[] Table =
        [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255)];

    [Fact]
    public void EarlyGifStreamEndKeepsDecodedPixelsAndLeavesTheRestClear()
    {
        var stream = RawGif.PackLzw((4, 3), (1, 3), (5, 3));
        var gif = RawGif.Build(2, 1, Table,
            new RawGif.FrameSpec(0, 0, 2, 1, [1, 0], Transparent: 0, Stream: stream));

        var frame = Assert.Single(GifDecoder.Decode(gif, out _, out _));

        Assert.Equal("green", RawGif.ColorAt(frame.Bgra, 2, 0, 0));
        Assert.Equal(0, frame.Bgra[7]);
    }

    [Fact]
    public void APartialFrameIsCompositedOntoTheWholeCanvas()
    {
        // 4x2 all red, then a 2x2 blue block over the left half.
        var gif = RawGif.Build(4, 2, Table,
            new RawGif.FrameSpec(0, 0, 4, 2, [0, 0, 0, 0, 0, 0, 0, 0]),
            new RawGif.FrameSpec(0, 0, 2, 2, [2, 2, 2, 2]));

        var frames = GifDecoder.Decode(gif, out int width, out int height);

        Assert.Equal(4, width);
        Assert.Equal(2, height);
        // The second frame is still the full screen, not the 2x2 the file stored.
        Assert.Equal(4 * 2 * 4, frames[1].Bgra.Length);
        Assert.Equal("blue", At(frames[1].Bgra, 4, 0, 0));
        Assert.Equal("blue", At(frames[1].Bgra, 4, 1, 1));
        Assert.Equal("red", At(frames[1].Bgra, 4, 3, 0));  // outside the frame's rectangle
        Assert.Equal("red", At(frames[1].Bgra, 4, 2, 1));
    }

    [Fact]
    public void RestoreToBackgroundClearsTheFrameItFollows()
    {
        // The first frame asks to be cleared, so frame 2 shows only its own block.
        var gif = RawGif.Build(4, 2, Table,
            new RawGif.FrameSpec(0, 0, 4, 2, [0, 0, 0, 0, 0, 0, 0, 0], Disposal: 2),
            new RawGif.FrameSpec(0, 0, 2, 2, [2, 2, 2, 2]));

        var frames = GifDecoder.Decode(gif, out _, out _);

        Assert.Equal("blue", At(frames[1].Bgra, 4, 0, 0));
        Assert.Equal("blue", At(frames[1].Bgra, 4, 1, 1));
        Assert.Equal("clear", At(frames[1].Bgra, 4, 3, 0)); // background, not red
        Assert.Equal("clear", At(frames[1].Bgra, 4, 2, 1));
    }

    [Fact]
    public void KeepDisposalLeavesTheFrameUnderneath()
    {
        var gif = RawGif.Build(4, 2, Table,
            new RawGif.FrameSpec(0, 0, 4, 2, [0, 0, 0, 0, 0, 0, 0, 0], Disposal: 1),
            new RawGif.FrameSpec(3, 0, 1, 2, [1, 1]));

        var frames = GifDecoder.Decode(gif, out _, out _);

        Assert.Equal("green", At(frames[1].Bgra, 4, 3, 0));
        Assert.Equal("red", At(frames[1].Bgra, 4, 0, 0));
    }

    [Fact]
    public void TheTransparentIndexLeavesEarlierPixelsAlone()
    {
        // Frame 2 covers the whole screen but every pixel uses the transparent
        // index, so it draws nothing and the red from frame 1 shows through.
        var gif = RawGif.Build(2, 1, Table,
            new RawGif.FrameSpec(0, 0, 2, 1, [0, 0]),
            new RawGif.FrameSpec(0, 0, 2, 1, [1, 1], Transparent: 1));

        var frames = GifDecoder.Decode(gif, out _, out _);

        Assert.Equal("red", At(frames[1].Bgra, 2, 0, 0));
        Assert.Equal("red", At(frames[1].Bgra, 2, 1, 0));
    }

    [Fact]
    public void TransparentPixelsDoNotCoverWhatIsUnderThem()
    {
        // Frame 1 is all red. Frame 2 draws green on the right and leaves the
        // left transparent, so the left keeps its red. Frame 3 covers the left
        // with red and leaves the right transparent, so the right keeps green.
        var gif = RawGif.Build(2, 1, Table,
            new RawGif.FrameSpec(0, 0, 2, 1, [0, 0]),
            new RawGif.FrameSpec(0, 0, 2, 1, [0, 1], Transparent: 0),
            new RawGif.FrameSpec(0, 0, 2, 1, [0, 1], Transparent: 1));

        var frames = GifDecoder.Decode(gif, out _, out _);

        Assert.Equal("red", At(frames[1].Bgra, 2, 0, 0));   // left untouched
        Assert.Equal("green", At(frames[1].Bgra, 2, 1, 0)); // right drawn
        Assert.Equal("red", At(frames[2].Bgra, 2, 0, 0));   // left redrawn
        Assert.Equal("green", At(frames[2].Bgra, 2, 1, 0)); // right still shows through
    }

    [Fact]
    public void CountMatchesTheDecodedFrames()
    {
        var gif = RawGif.Build(4, 2, Table,
            new RawGif.FrameSpec(0, 0, 4, 2, [0, 0, 0, 0, 0, 0, 0, 0]),
            new RawGif.FrameSpec(0, 0, 2, 2, [2, 2, 2, 2]),
            new RawGif.FrameSpec(3, 0, 1, 2, [1, 1]));

        Assert.Equal(3, GifImport.FrameCount(gif));
        Assert.Equal(3, GifDecoder.Decode(gif, out _, out _).Count);
    }

    /// <summary>
    /// Names the color at a pixel, so the assertions read as colors instead
    /// of BGRA channel offsets. "clear" is a fully transparent pixel.
    /// </summary>
    private static string At(byte[] bgra, int width, int x, int y)
    {
        int o = (y * width + x) * 4;
        if (bgra[o + 3] == 0) return "clear";
        return (bgra[o + 2], bgra[o + 1], bgra[o]) switch
        {
            (255, 0, 0) => "red",
            (0, 255, 0) => "green",
            (0, 0, 255) => "blue",
            _ => $"rgb({bgra[o + 2]},{bgra[o + 1]},{bgra[o]})",
        };
    }
}

public class GifInterlaceTests
{
    private static RawGif.TableEntry[] Table => RawGif.FourColors;

    /// <summary>Indexes that give every row its own color, so a row that lands
    /// in the wrong place shows it.</summary>
    private static byte[] OneColorPerRow(int width, int height, int offset = 0) =>
        [.. Enumerable.Range(0, width * height).Select(i => (byte)((i / width + offset) % 4))];

    private static string ColorAt(byte[] bgra, int width, int x, int y) =>
        RawGif.ColorAt(bgra, width, x, y);

    private static string Color(int index) => RawGif.ColorName(index);

    [Fact]
    public void AnInterlacedFrameLandsOnTheRowsItBelongsTo()
    {
        // Four rows: the passes store row 0, then row 2, then rows 1 and 3.
        // Reading that order as-is would put row 2's pixels on row 1.
        var gif = RawGif.Build(4, 4, Table,
            new RawGif.FrameSpec(0, 0, 4, 4, OneColorPerRow(4, 4), Interlaced: true));

        var bgra = GifDecoder.Decode(gif, out int width, out _)[0].Bgra;

        for (int y = 0; y < 4; y++)
            Assert.Equal(Color(y), ColorAt(bgra, width, 0, y));
    }

    [Fact]
    public void AnInterlacedFrameSpansAllFourPasses()
    {
        // Nine rows, so the passes take row 0; then 4 and 8; then 2 and 6; then
        // 1, 3, 5 and 7.
        var gif = RawGif.Build(4, 9, Table,
            new RawGif.FrameSpec(0, 0, 4, 9, OneColorPerRow(4, 9), Interlaced: true));

        var bgra = GifDecoder.Decode(gif, out int width, out _)[0].Bgra;

        for (int y = 0; y < 9; y++)
            Assert.Equal(Color(y), ColorAt(bgra, width, 2, y));
    }

    [Fact]
    public void InterlacingAppliesToEachFrameOnItsOwn()
    {
        // The first frame is stored in order, the second interlaced, and each
        // frame's own rows are what has to come out right.
        var gif = RawGif.Build(4, 9, Table,
            new RawGif.FrameSpec(0, 0, 4, 9, OneColorPerRow(4, 9)),
            new RawGif.FrameSpec(0, 0, 4, 9, OneColorPerRow(4, 9, offset: 1), Interlaced: true));

        var frames = GifDecoder.Decode(gif, out int width, out _);

        for (int y = 0; y < 9; y++)
        {
            Assert.Equal(Color(y), ColorAt(frames[0].Bgra, width, 1, y));
            Assert.Equal(Color(y + 1), ColorAt(frames[1].Bgra, width, 1, y));
        }
    }
}

public class GifLzwTests
{
    [Fact]
    public void ACodeJustPastTheEndOfTheTableAppendsTheFirstPixel()
    {
        // Minimum code size 2, so 0-3 are pixels, 4 clears, 5 ends, and the
        // table fills from 6. Read in order, these codes build entry 6 = "12",
        // 7 = "21", 8 = "123" and 9 = "31". The 10 is one past the end of the
        // table: the entry it names is the one about to be created, so its
        // string is the previous code's string "123" plus that string's first
        // pixel, "1231". Putting that pixel anywhere but the end gives "1123",
        // which is two pixels out of place and reads as a run shifted along.
        // The later codes also sit past the width's first step from 3 to 4 bits,
        // so reading any of them at the wrong width fails the same assertions.
        var stream = RawGif.PackLzw((4, 3), (1, 3), (2, 3), (6, 3), (3, 4), (8, 4), (10, 4), (5, 4));
        var gif = RawGif.Build(4, 3, RawGif.FourColors,
            new RawGif.FrameSpec(0, 0, 4, 3, [], Stream: stream));

        var bgra = GifDecoder.Decode(gif, out int width, out _)[0].Bgra;

        int[] want = [1, 2, 1, 2, 3, 1, 2, 3, 1, 2, 3, 1];
        for (int i = 0; i < want.Length; i++)
            Assert.Equal(RawGif.ColorName(want[i]), RawGif.ColorAt(bgra, width, i % 4, i / 4));
    }
}
