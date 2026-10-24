using Sff;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class PaletteTemplateTests
{
    private static PaletteTemplate.Piece Piece(params byte[] row) =>
        PaletteTemplate.Trim(row, row.Length, 1)!;

    [Fact]
    public void TrimCropsToVisiblePixelsAndRecordsIndexes()
    {
        byte[] bitmap =
        [
            0, 0, 0, 0,
            0, 5, 0, 0,
            0, 0, 200, 0,
        ];
        var p = PaletteTemplate.Trim(bitmap, 4, 3)!;
        Assert.Equal((1, 1, 2, 2), (p.X, p.Y, p.Width, p.Height));
        Assert.Same(bitmap, p.Source);
        var sheet = PaletteTemplate.Build([p], padding: 0, swatchCell: 1);
        Assert.Equal(new byte[] { 5, 0 }, sheet.Indices.Skip(16).Take(2).ToArray());
        Assert.Equal(new byte[] { 0, 200 }, sheet.Indices.Skip(sheet.Width + 16).Take(2).ToArray());
        Assert.Equal(1UL << 5, p.Used[0]);
        Assert.Equal(1UL << (200 & 63), p.Used[3]);
        Assert.Null(PaletteTemplate.Trim(new byte[4], 2, 2));
    }

    [Fact]
    public void ChooseCoversEveryUsedIndexWhenCountAllows()
    {
        var pieces = new List<PaletteTemplate.Piece>
        {
            Piece(1, 2), Piece(1), Piece(2), Piece(3, 4), Piece(4), Piece(9), Piece(1, 2, 3),
        };
        for (int seed = 0; seed < 20; seed++)
        {
            var s = PaletteTemplate.Choose(pieces, 3, seed);
            Assert.Equal(5, s.Used);
            Assert.Equal(5, s.Covered);
            Assert.Equal(3, s.NeededForAll);
            Assert.Equal(3, s.Chosen.Distinct().Count());
        }
    }

    [Fact]
    public void ChooseFillsRandomlyAndRepeatsForTheSameSeed()
    {
        var pieces = Enumerable.Range(1, 10).Select(i => Piece((byte)i)).ToList();
        var a = PaletteTemplate.Choose(pieces, 4, 7);
        var b = PaletteTemplate.Choose(pieces, 4, 7);
        Assert.Equal(a.Chosen, b.Chosen);
        Assert.Equal(4, a.Covered);
        Assert.Equal(10, a.NeededForAll);

        var all = PaletteTemplate.Choose(pieces, 50, 1);
        Assert.Equal(10, all.Chosen.Count);
        Assert.Equal(10, all.Covered);
    }

    [Fact]
    public void ChooseAlwaysTakesTheFirstPiece()
    {
        var pieces = new List<PaletteTemplate.Piece> { Piece(1, 2, 3), Piece(9), Piece(1) };
        for (int seed = 0; seed < 20; seed++)
        {
            var s = PaletteTemplate.Choose(pieces, 1, seed, first: 2);
            Assert.Equal(new List<int> { 2 }, s.Chosen);
            Assert.Equal(3, PaletteTemplate.Choose(pieces, 3, seed, first: 2).NeededForAll);
        }
    }

    [Fact]
    public void BuildPlacesSwatchTopLeftThenPieces()
    {
        var pieces = new List<PaletteTemplate.Piece> { Piece(9), Piece(7, 7, 7) };
        var sheet = PaletteTemplate.Build(pieces, padding: 2, swatchCell: 1);
        // Swatch 16 wide at x 2; pieces follow on its row (equal heights keep input order).
        Assert.Equal(2 + 16 + 2 + 1 + 2 + 3 + 2, sheet.Width);
        Assert.Equal(2 + 16 + 2, sheet.Height);
        var row = sheet.Indices.Skip(2 * sheet.Width + 18).Take(sheet.Width - 18).ToArray();
        Assert.Equal(new byte[] { 0, 0, 9, 0, 0, 7, 7, 7, 0, 0 }, row);
        // Swatch: entry i at (padding + i % 16, padding + i / 16).
        for (int i = 0; i < 256; i++)
            Assert.Equal((byte)i, sheet.Indices[(2 + i / 16) * sheet.Width + 2 + i % 16]);
    }

    [Fact]
    public void BuildPlacesTheFirstPieceRightAfterTheSwatch()
    {
        var pieces = new List<PaletteTemplate.Piece> { Piece(9), Piece(7, 7, 7) };
        var sheet = PaletteTemplate.Build(pieces, padding: 0, swatchCell: 1, first: 1);
        var row = sheet.Indices.Skip(16).Take(sheet.Width - 16).ToArray();
        Assert.Equal(new byte[] { 7, 7, 7, 9 }, row);
    }

    [Fact]
    public void EncodeWritesAPaletteThatReadsBack()
    {
        var colors = new Rgba[] { new(255, 0, 255, 255), new(10, 20, 30, 255), new(40, 50, 60, 128) };
        var sheet = PaletteTemplate.Build([Piece(1, 2)], padding: 1, swatchCell: 2);
        var png = Png.Decode(PaletteTemplate.Encode(sheet, colors));
        Assert.Equal(sheet.Width, png.Width);
        Assert.Equal(sheet.Height, png.Height);
        Assert.NotNull(png.Plte);
        Assert.Equal(colors[2], png.Plte![2]);
        Assert.Equal(sheet.Indices, png.Indices);
    }
}
