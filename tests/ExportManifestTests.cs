using Editor;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>Other tools write manifests too, so what Read accepts must stay stable</summary>
public class ExportManifestTests
{
    private static ExportManifest.Entry Sprite(int g, int i, string file,
        (int, int)? axis = null, (int, int)? palette = null) => new("sprite", g, i, file, axis, palette);

    [Fact]
    public void WriteThenReadGivesTheSameEntries()
    {
        var entries = new List<ExportManifest.Entry>
        {
            new("palette", 1, 1, "pal 1_1.png"),
            Sprite(0, 0, "0_0.png", (10, -20), (1, 1)),
            Sprite(9000, 1, "sub/9000_1.pcx"),
        };
        var (read, errors) = ExportManifest.Read(ExportManifest.Write(entries));
        Assert.Empty(errors);
        Assert.Equal(entries, read);
    }

    [Fact]
    public void CommentsWorkOnTheirOwnLineAndAfterAValue()
    {
        var (read, errors) = ExportManifest.Read("""
            ; header
            [sprite 0,2] ; idle
            file = a.png ; first frame
              ; indented comment
            axis = 1,2;no space
            """);
        Assert.Empty(errors);
        Assert.Equal([Sprite(0, 2, "a.png", (1, 2))], read);
    }

    [Fact]
    public void SectionHeadersAllowSpacesAndTabs()
    {
        var (read, errors) = ExportManifest.Read(
            "[sprite 0, 2]\nfile = a.png\n[ sprite\t1,3 ]\nfile = b.png\n[sprite   4 , 5]\nfile = c.png\n");
        Assert.Empty(errors);
        Assert.Equal([Sprite(0, 2, "a.png"), Sprite(1, 3, "b.png"), Sprite(4, 5, "c.png")], read);
    }

    [Fact]
    public void TypesAndKeysIgnoreLetterCase()
    {
        var (read, errors) = ExportManifest.Read("[SPRITE 0,0]\nFILE = A.png\nAxis = 3,4\n");
        Assert.Empty(errors);
        Assert.Equal([Sprite(0, 0, "A.png", (3, 4))], read);
    }

    [Fact]
    public void FileValuesKeepSpacesAndSlashesAsWritten()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\nfile =  my dir\\my sprite.png  \n");
        Assert.Empty(errors);
        Assert.Equal("my dir\\my sprite.png", Assert.Single(read).File);
    }

    [Fact]
    public void CrlfAndLfReadTheSame()
    {
        const string text = "[sprite 0,0]\nfile = a.png\naxis = 1,2\n";
        Assert.Equal(ExportManifest.Read(text).Entries, ExportManifest.Read(text.Replace("\n", "\r\n")).Entries);
    }

    [Fact]
    public void NumbersAreNotRangeChecked()
    {
        var (read, errors) = ExportManifest.Read("[sprite -1,70000]\nfile = a.png\naxis = -40000,40000\n");
        Assert.Empty(errors);
        Assert.Equal([Sprite(-1, 70000, "a.png", (-40000, 40000))], read);
    }

    [Fact]
    public void EntryWithoutFileIsDroppedAndReported()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\naxis = 1,2\n[sprite 0,1]\nfile = b.png\n");
        Assert.Equal([Sprite(0, 1, "b.png")], read);
        Assert.Contains("line 1", Assert.Single(errors));
    }

    [Fact]
    public void EmptyFileValueIsReported()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\nfile =\n");
        Assert.Empty(read);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void DuplicateFieldKeepsTheFirstAndIsReported()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\nfile = a.png\nfile = b.png\naxis = 1,1\naxis = 2,2\n");
        Assert.Equal([Sprite(0, 0, "a.png", (1, 1))], read);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void UnknownFieldIsReportedButTheEntryIsKept()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\nfile = a.png\ncolor = red\n");
        Assert.Equal([Sprite(0, 0, "a.png")], read);
        Assert.Contains("line 3", Assert.Single(errors));
    }

    [Fact]
    public void BadValuesAreReportedAndLeftOut()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\nfile = a.png\naxis = 1\npalette = x,y\n");
        Assert.Equal([Sprite(0, 0, "a.png")], read);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void BadSectionDoesNotPassItsFieldsToThePreviousEntry()
    {
        var (read, errors) = ExportManifest.Read("[sprite 0,0]\nfile = a.png\n[sprite]\nfile = b.png\n[sprite 1]\n");
        Assert.Equal([Sprite(0, 0, "a.png")], read);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("line 3"));
        Assert.Contains(errors, e => e.Contains("line 4"));
        Assert.Contains(errors, e => e.Contains("line 5"));
    }

    [Fact]
    public void LinesOutsideSectionsOrWithoutEqualsAreReported()
    {
        var (read, errors) = ExportManifest.Read("file = a.png\n[sprite 0,0]\nfile = b.png\njunk\n");
        Assert.Equal([Sprite(0, 0, "b.png")], read);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void OtherEntryTypesAreReadForTheCallerToJudge()
    {
        var (read, errors) = ExportManifest.Read("[sound 0,0]\nfile = a.wav\n");
        Assert.Empty(errors);
        Assert.Equal("sound", Assert.Single(read).Type);
    }
}
