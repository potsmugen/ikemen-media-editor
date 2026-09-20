using System.Text;
using Air;
using Xunit;

namespace IkemenMediaEditor.Tests;

public class EmbeddedAirDocumentTests
{
    [Fact]
    public void ReplacesActionsAndPreservesDefinitionBytesAroundThem()
    {
        const string prefix = "; heading é\r\n[Info]\r\nname = untouched\r\n";
        const string first = "; first action\r\n[Begin Action 1]\r\n0,0, 0,0, 2\r\n";
        const string middle = "; background section comment\r\n[BG 0, left]\r\nspriteno = 4,5\r\n";
        const string second = "[Begin Action 2]\r\n0,1, 0,0, 3\r\n";
        const string suffix = "[Music]\r\nbgmusic = unchanged.ogg\r\n";
        var source = Encoding.UTF8.GetBytes(prefix + first + middle + second + suffix);
        var edited = AirParser.Parse("[Begin Action 1]\n0,9, 1,2, 4\n[Begin Action 2]\n0,8, 3,4, 5");

        var result = EmbeddedAirDocument.ReplaceActions(source, edited);

        string action1 = WindowsLines(AirWriter.WriteAction(edited.Actions[0]));
        string action2 = WindowsLines(AirWriter.WriteAction(edited.Actions[1]));
        Assert.Equal(prefix + action1 + middle + action2 + suffix, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void AddsAndRemovesActionBlocksWithoutChangingOtherSections()
    {
        const string prefix = "[Info]\nname = keep\n";
        const string first = "[Begin Action 1]\n0,0, 0,0, 2\n";
        const string middle = "[BG 0, layer]\nspriteno = 1,2\n";
        const string second = "[Begin Action 2]\n0,1, 0,0, 3\n";
        const string suffix = "[Music]\nbgmusic = keep.ogg\n";
        var source = Encoding.UTF8.GetBytes(prefix + first + middle + second + suffix);
        var oneAction = AirParser.Parse("[Begin Action 7]\n0,2, 0,0, 4");

        // This source is all-LF, so the action is written back with LF and its
        // trailing blank line kept.
        var removed = Encoding.UTF8.GetString(EmbeddedAirDocument.ReplaceActions(source, oneAction));
        Assert.Equal(prefix + LfLines(AirWriter.WriteAction(oneAction.Actions[0])) + middle + suffix, removed);

        var threeActions = AirParser.Parse("[Begin Action 7]\n0,2, 0,0, 4\n[Begin Action 8]\n0,3, 0,0, 5\n[Begin Action 9]\n0,4, 0,0, 6");
        var added = Encoding.UTF8.GetString(EmbeddedAirDocument.ReplaceActions(source, threeActions));
        Assert.Equal(prefix + LfLines(AirWriter.WriteAction(threeActions.Actions[0])) + middle
            + LfLines(AirWriter.WriteAction(threeActions.Actions[1]))
            + LfLinesWithBlank(AirWriter.WriteAction(threeActions.Actions[2])) + suffix, added);
    }

    [Fact]
    public void PreservesLegacyDefinitionEncodingOutsideActionBlocks()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(1252);
        const string prefix = "; stage café\r\n[Info]\r\nname = keep\r\n";
        const string action = "[Begin Action 1]\r\n0,0, 0,0, 2\r\n";
        const string suffix = "[BG 0, layer]\r\nspriteno = 1,2\r\n";
        var source = encoding.GetBytes(prefix + action + suffix);
        var edited = AirParser.Parse("[Begin Action 1]\n0,9, 0,0, 4");

        var result = EmbeddedAirDocument.ReplaceActions(source, edited);

        Assert.Equal(encoding.GetBytes(prefix + WindowsLines(AirWriter.WriteAction(edited.Actions[0])) + suffix), result);
    }

    [Fact]
    public void PreservesDefinitionByteOrderMark()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        const string before = "[Info]\nname = keep\n";
        const string after = "[BG 0, layer]\nspriteno = 1,2\n";
        var source = bom.Concat(Encoding.UTF8.GetBytes(before + "[Begin Action 1]\n0,0, 0,0, 2\n" + after)).ToArray();
        var edited = AirParser.Parse("[Begin Action 1]\n0,9, 0,0, 4");

        var result = EmbeddedAirDocument.ReplaceActions(source, edited);

        Assert.Equal(bom.Concat(Encoding.UTF8.GetBytes(before + LfLines(AirWriter.WriteAction(edited.Actions[0])) + after)), result);
    }

    [Fact]
    public void ABareSemicolonAboveAnActionHeaderIsTheActionName()
    {
        // AirParser reads an empty ";" above a header as that action's empty
        // name, so block detection must agree or the ";" is both left in the
        // surrounding bytes and written again inside the block.
        const string before = "[Info]\nname = keep\n";
        const string first = "[Begin Action 1]\n0,0, 0,0, 2\n";
        const string name = ";\n";
        const string second = "[Begin Action 2]\n0,1, 0,0, 3\n";
        const string after = "[Music]\nbgmusic = keep.ogg\n";
        var source = Encoding.UTF8.GetBytes(before + first + name + second + after);
        var parsed = AirParser.Parse(Encoding.UTF8.GetString(source));

        Assert.Equal([""], parsed.Actions[1].Comments);
        Assert.Equal(source, EmbeddedAirDocument.ReplaceActions(source, parsed));
    }

    [Fact]
    public void CommentsAroundAnActionBlockAreLeftExactlyAsTheyWere()
    {
        // Actions are embedded in a larger definition, so only the action's own
        // lines may be rewritten: notes before a header, rules above it and
        // comments around neighboring sections all stay put and byte for byte.
        const string source = """
            [Info]
            name = keep
            [Begin Action 1]
            1,0, 0,0, 4
            ; older note
            ; real name
            [Begin Action 2]
            2,0, 0,0, 4
            ; ------
            ; note for the background
            [BG 0, layer]
            spriteno = 1,2
            ; trailing on the section
            [Begin Action 3]
            3,0, 0,0, 4
            ; note after the last action
            [Music]
            bgmusic = keep.ogg

            """;
        var bytes = Encoding.UTF8.GetBytes(source);
        var parsed = AirParser.Parse(source);

        Assert.Equal(["real name"], parsed.Actions[1].Comments);            // only the nearer line
        Assert.Equal(["trailing on the section"], parsed.Actions[2].Comments); // nearest again
        Assert.Equal(bytes, EmbeddedAirDocument.ReplaceActions(bytes, parsed));
    }

    [Fact]
    public void ExistingActionObjectsStayAtTheirOriginalSourceLocations()
    {
        const string prefix = "[Info]\nname = keep\n";
        const string first = "[Begin Action 1]\n0,0, 0,0, 2\n";
        const string middle = "[BG 0, layer]\nspriteno = 1,2\n";
        const string second = "[Begin Action 2]\n0,1, 0,0, 3\n";
        const string suffix = "[Music]\nbgmusic = keep.ogg\n";
        var source = Encoding.UTF8.GetBytes(prefix + first + middle + second + suffix);
        var original = AirParser.Parse("[Begin Action 1]\n0,0, 0,0, 2\n[Begin Action 2]\n0,1, 0,0, 3");
        var added = AirParser.Parse("[Begin Action 9]\n0,9, 0,0, 4").Actions[0];
        var live = new AirFile { Actions = [original.Actions[1], added, original.Actions[0]] };

        var result = EmbeddedAirDocument.ReplaceActions(source, live, original.Actions, out var writtenOrder);

        string expected = prefix + LfLines(AirWriter.WriteAction(original.Actions[0])) + middle
            + LfLines(AirWriter.WriteAction(original.Actions[1]))
            + LfLinesWithBlank(AirWriter.WriteAction(added)) + suffix;
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
        Assert.Equal([original.Actions[0], original.Actions[1], added], writtenOrder);
    }

    private static string LfLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n') + "\n";

    private static string LfLinesWithBlank(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string WindowsLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
}
