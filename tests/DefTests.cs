using Def;
using Xunit;

namespace IkemenMediaEditor.Tests;

/// <summary>
/// DefFile is read-only and deliberately doesn't validate against the real
/// .def grammar beyond section/key=value — these check it finds the right
/// keys in the right sections for both a character .def ([Files]) and a
/// stage one ([BGdef]), and correctly tells the two apart.
/// </summary>
public class DefTests
{
    private const string CharacterDef = """
        [Info]
        name = "kfm"
        displayname = "Kung Fu Man"
        author = "Elecbyte"
        mugenversion = 1.1

        [Files]
        sprite = kfm.sff
        anim = kfm.air
        sound = kfm.snd
        pal1 = kfm1.act
        pal2 = kfm2.act
        """;

    private const string StageDef = """
        [Info]
        name = "stage0"
        displayname = "Training Room"

        [BGdef]
        spr = stage0.sff
        debugbg = 0

        [BG floor]
        type = normal
        spriteno = 0,0
        layerno = 0
        """;

    // Trimmed from a real screenpack system.def (Cosmic Drift).
    private const string ScreenpackDef = """
        [Info]
        name = "Cosmic Drift"
        author = "Phantom.of.the.Server"

        [Files]
        spr = system.sff
        snd = system.snd
        select = select.def
        fight = ../mugen1_lifebar_double_remix/fight_43.def
        """;

    // Trimmed from a real lifebar fight.def (MUGEN1 Lifebar Double Remix).
    private const string LifebarDef = """
        [Info]
        name = "MUGEN1 Lifebar Double Remix"
        author = "Phantom.of.the.Server"

        [Files]
        sff = fight.sff
        snd = nocturnis.snd
        fightfx.sff = fightfx.sff
        fightfx.air = fightfx.air
        common.snd = common.snd
        """;

    // Trimmed from Ikemen GO's data/gofx/gofx.def, a common FX package.
    private const string CommonFxDef = """
        [Info]
        prefix = GO
        fx.scale = 1
        localcoord = 320, 240

        [Files]
        sff = gofx.sff
        air = gofx.air
        snd = gofx.snd
        """;

    // Trimmed from a real storyboard .def (evilkenintro.def) — no [Info],
    // no [Files]; a storyboard's [SceneDef]/[Scene N] blocks are its whole
    // structure.
    private const string StoryboardDef = """
        [SceneDef]
        spr = evilkenintro.sff
        startscene = 0

        [Scene 0]
        fadein.time = 60
        layer0.anim = 0
        end.time = 600
        """;

    [Fact]
    public void ParsesACharacterDef()
    {
        var def = DefFile.Parse(CharacterDef, "/kfm");

        Assert.Equal("kfm", def.Name);
        Assert.Equal("Kung Fu Man", def.DisplayName);
        Assert.Equal("Elecbyte", def.Author);
        Assert.Equal(Path.Combine("/kfm", "kfm.sff"), def.SpritePath);
        Assert.Equal(Path.Combine("/kfm", "kfm.air"), def.AnimationPath);
        Assert.Equal(Path.Combine("/kfm", "kfm.snd"), def.SoundPath);
        Assert.Equal([(1, Path.Combine("/kfm", "kfm1.act")), (2, Path.Combine("/kfm", "kfm2.act"))], def.Palettes);
        Assert.Null(def.StageSpritePath); // a character def has no [BGdef] at all
    }

    [Fact]
    public void ReadsLeadingZeroPaletteKeysPreferringThePlainOne()
    {
        var def = DefFile.Parse("""
            [Files]
            pal01 = a1.act
            pal1 = b1.act
            pal02 = a2.act
            pal010 = a10.act
            """, "/kfm");

        Assert.Equal([(1, Path.Combine("/kfm", "b1.act")), (2, Path.Combine("/kfm", "a2.act")), (10, Path.Combine("/kfm", "a10.act"))],
            def.Palettes);
    }

    [Fact]
    public void ParsesAStageDef()
    {
        var def = DefFile.Parse(StageDef, "/stages");

        Assert.Equal("stage0", def.Name);
        Assert.Equal("Training Room", def.DisplayName);
        Assert.Equal(Path.Combine("/stages", "stage0.sff"), def.StageSpritePath);
        // A stage def has no [Files] section — nothing to derive these from.
        Assert.Null(def.SpritePath);
        Assert.Null(def.AnimationPath);
        Assert.Null(def.SoundPath);
        Assert.Empty(def.Palettes);
    }

    [Fact]
    public void ParsesAScreenpackDef()
    {
        var def = DefFile.Parse(ScreenpackDef, "/screenpack");

        Assert.Equal("Cosmic Drift", def.Name);
        Assert.Equal(Path.Combine("/screenpack", "system.sff"), def.ScreenpackSpritePath);
        Assert.Equal(Path.Combine("/screenpack", "system.snd"), def.ScreenpackSoundPath);
        // A screenpack def has no character [Files] sprite=/sound=, no
        // [BGdef], no [SceneDef].
        Assert.Null(def.SpritePath);
        Assert.Null(def.SoundPath);
        Assert.Null(def.StageSpritePath);
        Assert.Null(def.StoryboardSpritePath);
    }

    [Fact]
    public void ParsesALifebarDef()
    {
        var def = DefFile.Parse(LifebarDef, "/lifebar");

        Assert.Equal("MUGEN1 Lifebar Double Remix", def.Name);
        Assert.Equal(Path.Combine("/lifebar", "fight.sff"), def.LifebarSpritePath);
        Assert.Equal(Path.Combine("/lifebar", "nocturnis.snd"), def.LifebarSoundPath);
        // The common-effects files [Files] also carries are deliberately not
        // modeled — out of scope (see AGENTS.md).
        Assert.Null(def.SpritePath);
        Assert.Null(def.StageSpritePath);
        Assert.Null(def.ScreenpackSpritePath);
        Assert.Null(def.StoryboardSpritePath);
        // fightfx.sff/fightfx.air are not the plain sff/air keys, so this
        // stays a lifebar.
        Assert.Null(def.CommonFxSpritePath);
        Assert.Null(def.CommonFxAnimationPath);
    }

    [Fact]
    public void ParsesACommonFxDef()
    {
        var def = DefFile.Parse(CommonFxDef, "/gofx");

        Assert.Equal(Path.Combine("/gofx", "gofx.sff"), def.CommonFxSpritePath);
        Assert.Equal(Path.Combine("/gofx", "gofx.air"), def.CommonFxAnimationPath);
        Assert.Equal(Path.Combine("/gofx", "gofx.snd"), def.CommonFxSoundPath);
        // The same sff/snd keys belong to a lifebar otherwise; air is what
        // tells the two formats apart.
        Assert.Null(def.LifebarSpritePath);
        Assert.Null(def.LifebarSoundPath);
        Assert.Null(def.SpritePath);
        Assert.Null(def.StageSpritePath);
        Assert.Null(def.ScreenpackSpritePath);
        Assert.Null(def.StoryboardSpritePath);

        Assert.Equal(
            [Path.Combine("/gofx", "gofx.sff"), Path.Combine("/gofx", "gofx.air"), Path.Combine("/gofx", "gofx.snd")],
            def.MissingFiles());
    }

    [Fact]
    public void SoundBelongsToTheDefKindWhoseMarkerKeyIsPresent()
    {
        var lifebar = DefFile.Parse("[Files]\nsff = fight.sff\nsnd = fight.snd\n", "/lifebar");
        Assert.Equal(Path.Combine("/lifebar", "fight.snd"), lifebar.LifebarSoundPath);
        Assert.Null(lifebar.ScreenpackSoundPath);
        Assert.Null(lifebar.CommonFxSoundPath);

        var screenpack = DefFile.Parse("[Files]\nspr = system.sff\nsnd = system.snd\n", "/screenpack");
        Assert.Equal(Path.Combine("/screenpack", "system.snd"), screenpack.ScreenpackSoundPath);
        Assert.Null(screenpack.LifebarSoundPath);
        Assert.Null(screenpack.CommonFxSoundPath);

        // No marker key: an snd on its own belongs to no known def kind.
        var orphan = DefFile.Parse("[Files]\nsnd = orphan.snd\n", "/orphan");
        Assert.Null(orphan.ScreenpackSoundPath);
        Assert.Null(orphan.LifebarSoundPath);
        Assert.Null(orphan.CommonFxSoundPath);
    }

    [Fact]
    public void ParsesAStoryboardDef()
    {
        var def = DefFile.Parse(StoryboardDef, "/storyboards");

        Assert.Equal(Path.Combine("/storyboards", "evilkenintro.sff"), def.StoryboardSpritePath);
        // No [Info] at all in a real storyboard def, and no sound
        // counterpart — scenes reference bgm= mp3s directly.
        Assert.Null(def.Name);
        Assert.Null(def.SpritePath);
        Assert.Null(def.SoundPath);
        Assert.Null(def.StageSpritePath);
        Assert.Null(def.ScreenpackSpritePath);
        Assert.Null(def.LifebarSpritePath);
    }

    [Fact]
    public void StageAndCharacterSpritePathsAreHowTheTwoFormatsAreToldApart()
    {
        // OnOpenStage/OpenCharacterFile (and "Open Recent"'s routing) rely on
        // exactly this: whichever of the two is null says which kind of .def
        // this is.
        var character = DefFile.Parse(CharacterDef, "/kfm");
        var stage = DefFile.Parse(StageDef, "/stages");

        Assert.NotNull(character.SpritePath);
        Assert.Null(character.StageSpritePath);
        Assert.Null(stage.SpritePath);
        Assert.NotNull(stage.StageSpritePath);
    }

    [Fact]
    public void AllSixDefFormatsAreToldApartByExactlyOneSpritePathField()
    {
        // DetectDefKind checks SpritePath, then StageSpritePath, then
        // CommonFxSpritePath, then ScreenpackSpritePath/LifebarSpritePath/
        // StoryboardSpritePath, each assuming the earlier ones already ruled
        // themselves out — which only holds if exactly one of the six is ever
        // set for a given .def. This is that assumption, checked directly.
        var character = DefFile.Parse(CharacterDef, "/kfm");
        var stage = DefFile.Parse(StageDef, "/stages");
        var commonFx = DefFile.Parse(CommonFxDef, "/gofx");
        var screenpack = DefFile.Parse(ScreenpackDef, "/screenpack");
        var lifebar = DefFile.Parse(LifebarDef, "/lifebar");
        var storyboard = DefFile.Parse(StoryboardDef, "/storyboards");

        Assert.NotNull(character.SpritePath);
        Assert.Null(character.StageSpritePath);
        Assert.Null(character.CommonFxSpritePath);
        Assert.Null(character.ScreenpackSpritePath);
        Assert.Null(character.LifebarSpritePath);
        Assert.Null(character.StoryboardSpritePath);

        Assert.Null(stage.SpritePath);
        Assert.NotNull(stage.StageSpritePath);
        Assert.Null(stage.CommonFxSpritePath);
        Assert.Null(stage.ScreenpackSpritePath);
        Assert.Null(stage.LifebarSpritePath);
        Assert.Null(stage.StoryboardSpritePath);

        Assert.Null(commonFx.SpritePath);
        Assert.Null(commonFx.StageSpritePath);
        Assert.NotNull(commonFx.CommonFxSpritePath);
        Assert.Null(commonFx.ScreenpackSpritePath);
        Assert.Null(commonFx.LifebarSpritePath);
        Assert.Null(commonFx.StoryboardSpritePath);

        Assert.Null(screenpack.SpritePath);
        Assert.Null(screenpack.StageSpritePath);
        Assert.Null(screenpack.CommonFxSpritePath);
        Assert.NotNull(screenpack.ScreenpackSpritePath);
        Assert.Null(screenpack.LifebarSpritePath);
        Assert.Null(screenpack.StoryboardSpritePath);

        Assert.Null(lifebar.SpritePath);
        Assert.Null(lifebar.StageSpritePath);
        Assert.Null(lifebar.CommonFxSpritePath);
        Assert.Null(lifebar.ScreenpackSpritePath);
        Assert.NotNull(lifebar.LifebarSpritePath);
        Assert.Null(lifebar.StoryboardSpritePath);

        Assert.Null(storyboard.SpritePath);
        Assert.Null(storyboard.StageSpritePath);
        Assert.Null(storyboard.CommonFxSpritePath);
        Assert.Null(storyboard.ScreenpackSpritePath);
        Assert.Null(storyboard.LifebarSpritePath);
        Assert.NotNull(storyboard.StoryboardSpritePath);
    }

    [Fact]
    public void ResolvesBackslashPathsRelativeToTheDefsFolder()
    {
        var def = DefFile.Parse("""
            [Files]
            sprite = chars\kfm\kfm.sff
            """, "/mugen");

        Assert.Equal(Path.Combine("/mugen", "chars", "kfm", "kfm.sff"), def.SpritePath);
    }

    [Fact]
    public void MissingFilesReportsOnlyWhatIsNotOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"def-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "kfm.sff"), "");
            // kfm.air and kfm.snd deliberately left absent.
            var def = DefFile.Parse("""
                [Files]
                sprite = kfm.sff
                anim = kfm.air
                sound = kfm.snd
                """, dir);

            var missing = def.MissingFiles().ToList();
            Assert.DoesNotContain(def.SpritePath, missing);
            Assert.Contains(def.AnimationPath, missing);
            Assert.Contains(def.SoundPath, missing);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OpenReadsFromDiskAndResolvesPathsAgainstItsOwnFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"def-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var defPath = Path.Combine(dir, "stage0.def");
        var sffPath = Path.Combine(dir, "stage0.sff");
        try
        {
            File.WriteAllText(defPath, StageDef);
            File.WriteAllText(sffPath, "");
            var def = DefFile.Open(defPath);

            Assert.NotNull(def.StageSpritePath);
            // Resolved against the .def's own folder, not the working directory —
            // checked functionally (the file it points at is really there) rather
            // than by comparing Directory as a string, which a symlinked temp
            // path (e.g. macOS's /tmp -> /private/tmp) could make flaky.
            Assert.True(File.Exists(def.StageSpritePath));
            Assert.Empty(def.MissingFiles());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WrongCaseReferencesResolveToTheFileOnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("deftest-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Sprites"));
            File.WriteAllText(Path.Combine(dir, "Sprites", "KFM.sff"), "");
            var def = DefFile.Parse("[Files]\nsprite = sprites\\kfm.SFF\n", dir);

            Assert.True(File.Exists(def.SpritePath));
            Assert.Empty(def.MissingFiles());
            if (!File.Exists(Path.Combine(dir, "sprites", "kfm.SFF"))) // case-sensitive file system
                Assert.Single(def.CaseMatches);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SeveralCaseMatchesStayMissing()
    {
        if (!OperatingSystem.IsLinux()) return; // needs a case-sensitive file system
        var dir = Directory.CreateTempSubdirectory("deftest-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.sff"), "");
            File.WriteAllText(Path.Combine(dir, "A.sff"), "");
            var def = DefFile.Parse("[Files]\nsprite = A.SFF\n", dir);

            Assert.Single(def.MissingFiles());
            Assert.Single(def.CaseAmbiguous);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
