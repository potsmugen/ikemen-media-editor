using Editor;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>A bad value drops only its key; broken JSON loads nothing.</summary>
public class SettingsTests
{
    [Fact]
    public void WrongTypeDropsOnlyThatKey()
    {
        var notes = new List<string>();
        var s = Settings.Parse("""
            { "Files": { "BackupCount": "x", "RecoveryIntervalMinutes": 9 },
              "Canvas": { "ZoomAroundCursor": false, "PinchZoomStep": [1] } }
            """, notes);
        Assert.NotNull(s);
        Assert.Equal(Settings.DefaultBackupCount, s.Files.BackupCount);
        Assert.Equal(9, s.Files.RecoveryIntervalMinutes);
        Assert.False(s.Canvas.ZoomAroundCursor);
        Assert.Equal(Settings.DefaultPinchZoomStep, s.Canvas.PinchZoomStep);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void BadArrayItemDropsTheArray()
    {
        var notes = new List<string>();
        var s = Settings.Parse("""{ "Canvas": { "ZoomLevels": [1, "two", 3], "ShowMask": true } }""", notes);
        Assert.NotNull(s);
        Assert.Equal(Settings.DefaultZoomLevels, s.Canvas.ZoomLevels);
        Assert.True(s.Canvas.ShowMask);
    }

    [Fact]
    public void BadShortcutDropsOnlyThatEntry()
    {
        var notes = new List<string>();
        var s = Settings.Parse("""{ "Shortcuts": { "a": 5, "b": "Ctrl+B" } }""", notes);
        Assert.NotNull(s);
        Assert.False(s.Shortcuts.ContainsKey("a"));
        Assert.Equal("Ctrl+B", s.Shortcuts["b"]);
    }

    [Fact]
    public void BrokenJsonLoadsNothing()
    {
        var notes = new List<string>();
        Assert.Null(Settings.Parse("""{ "Files": { "BackupCount": 2 """, notes));
        Assert.Single(notes);
    }
}
