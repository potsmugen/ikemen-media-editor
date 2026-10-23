using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("IkemenMediaEditor.Tests")]

namespace Editor;

/// <summary>
/// Source-generated metadata for settings.json. The trimmer cannot see a
/// property that only System.Text.Json would read by reflection, so a trimmed
/// build can silently drop one — a setting that then stops persisting, with no
/// error anywhere. A generated context emits the property accessors as real
/// code, so the trimmer sees them, and a rename that breaks serialization
/// becomes a compile error instead of lost data.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
// Nested in Settings, but in the Sff assembly: rooting Editor would not keep these.
[JsonSerializable(typeof(Sff.SprMakerOptions))]
[JsonSerializable(typeof(Sff.SprMaker2Options))]
[JsonSerializable(typeof(RecoveryManifest))]
[JsonSerializable(typeof(RecoveryStore.ActivePackage))]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SettingsContext : JsonSerializerContext;

/// <summary>
/// Preferences that survive a restart, kept as JSON under the user's roaming
/// profile. Nothing here is essential, so every failure is swallowed rather
/// than bothering the user.
/// </summary>
/// <summary>Where a floating panel's window was, and whether it was floating at exit.</summary>
public sealed class FloatPanelPlacement
{
    public bool Floating { get; set; }
    public bool RolledUp { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }  // expanded size, even while rolled up
    public double Height { get; set; }
}

public sealed class Settings
{
    // Defaults and fixed limits. Each use site clamps nonsense values with Checked, so a typo
    // in settings.json can't wedge the editor.

    // General
    public const int DefaultMaxRecentFiles = 10, DefaultUndoLimit = 200, DefaultSplashMilliseconds = 1000;
    public const double DefaultUndoMergeSeconds = 1, DefaultTabDragThresholdPixels = 6;
    /// <summary>Scroll bars shrink to a thin line when the pointer leaves (Avalonia's auto-hide).
    /// Read at startup; false keeps them full width.</summary>
    public const bool DefaultScrollBarsAutoHide = true;

    // Files
    public const int DefaultBackupCount = 1, DefaultBackupCountMax = 10, DefaultRecoveryIntervalMinutes = 5;

    // Layout
    public const double DefaultWindowWidth = 1100, DefaultWindowHeight = 700;
    public const double DefaultPanelFitMinWidth = 120, DefaultPanelFitMaxPercent = 40;

    // Appearance
    public const double DefaultFontScalePercent = 100, DefaultButtonScalePercent = 100,
        DefaultUiScaleMinPercent = 75, DefaultUiScaleMaxPercent = 200;
    public const double DefaultButtonGap = 4, DefaultButtonGapMax = 24;
    public const double DefaultPanelSeparatorWidth = 2, DefaultPanelSeparatorWidthMax = 10;
    public const double DefaultPanelContentInset = 8, DefaultPanelContentInsetMax = 20;

    // Canvas
    public static readonly double[] DefaultZoomLevels = [0.125, 0.25, 0.5, 1, 2, 3, 4, 6, 8, 12, 16];
    public const double DefaultSelectionDragThresholdPixels = 3;
    public const double DefaultCrosshairArmLength = 7, DefaultCrosshairThickness = 1,
        DefaultCrosshairArmMin = 1, DefaultCrosshairArmMax = 100,
        DefaultCrosshairThicknessMin = 1, DefaultCrosshairThicknessMax = 10,
        DefaultBackgroundOpacityPercent = 100;
    public const int DefaultBackgroundOffsetMax = 10000;
    public const int DefaultCheckerSize = 32, CheckerSizeMin = 2, CheckerSizeMax = 256;

    // Sprites
    public const double DefaultOnionOpacity = 0.5, DefaultSpriteSwatchCellSize = 16, DefaultFillScrollMilliseconds = 60;
    /// <summary>Paint Mode's own undo steps kept per pending session; the oldest drop off.</summary>
    public const int DefaultPaintUndoLimit = 100;
    public const int DefaultSimilarExact = 100, DefaultSimilarNear = 90, DefaultSimilarLoose = 80;
    public const int DefaultSpriteScalePercent = 100, DefaultSpriteScaleMinPercent = 1, DefaultSpriteScaleMaxPercent = 1000;
    public const int DefaultColorReplacePairs = 10;
    public const int DefaultNearestColorAlphaThreshold = Sff.ImageImport.DefaultTransparentAlphaThreshold;
    public const int DefaultSheetMinimumSizePixels = Sff.SheetImport.DefaultMinimumSize;

    // Palettes
    public const double DefaultPaletteCellMin = 6, DefaultPaletteCellMax = 64;
    public const bool DefaultPalettePerceptualHue = true;
    public const int DefaultPalettePngSwatchSize = 16;
    public const int DefaultColorPickerBoxWidth = 256, DefaultColorPickerBoxHeight = 256;

    // Animations
    public const int DefaultAutoBoxes = 3, DefaultAutoBoxesAlphaThreshold = 32, DefaultTextViewRespanMilliseconds = 250;
    public const double DefaultTimelineMaxPixelsPerTick = 16, DefaultTimelineZoomStep = 1.5, DefaultTimelineScrollStep = 0.1,
        DefaultTimelineDragThresholdPixels = 3, DefaultTimelineTickMarkMinPixels = 4;
    public const int DefaultGifScale = 1, DefaultGifScaleMax = 8, DefaultGifMaxSide = 4096;

    // Sounds
    public const double DefaultPitchMaxSemitones = 24, DefaultRateMinPercent = 25, DefaultRateMaxPercent = 400;
    public const double DefaultWaveformMaxPixelsPerSample = 8, DefaultWaveformZoomStep = 1.5, DefaultWaveformScrollStep = 0.1;
    public const double DefaultFadeMilliseconds = 100, DefaultSelectionCrossfadeMilliseconds = 5,
        DefaultWaveformDragThresholdPixels = 3, DefaultVolumeMaxPercent = 1000;
    public const double DefaultEchoDelayMilliseconds = 250, DefaultEchoDecayPercent = 50, DefaultEchoDecayStep = 5;
    public const int DefaultEchoMaxRepeats = 16, DefaultDownsampleAllRate = 44100;
    public const double DefaultInsertSilenceMilliseconds = 100, DefaultInsertSilenceMaxMilliseconds = 60000;
    /// <summary>Windows plays in-process; elsewhere an external player has to start first, so it waits longer.</summary>
    public static double DefaultPlaybackLatencyMilliseconds => OperatingSystem.IsWindows() ? 100 : 150;

    /// <summary>Changed keyboard shortcuts only, command id to text like "Ctrl+Shift+Z"; "" removes
    /// the shortcut. See Shortcuts.cs; Preferences > Shortcuts writes it.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    /// <summary>A setting's value when ok accepts it; otherwise its default, with the bad value logged.</summary>
    public static T Checked<T>(string name, T value, T fallback, Func<T, bool> ok)
    {
        if (ok(value)) return value;
        CrashLog.Error("Settings", $"{name} = {value} isn't valid; using {fallback}");
        return fallback;
    }

    /// <summary>Where the JSON lives. Creating the directory here keeps every caller simple.</summary>
    public static string FilePath
    {
        get
        {
            return AppPaths.DataFile("settings.json");
        }
    }

    /// <summary>Unknown keys are ignored, so a settings.json from before the sections falls back to defaults.</summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsContext.Default.Settings) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Settings.Load", ex);
        }
        return new Settings();
    }

    public static void ResetToDefaults()
    {
        try
        {
            var defaults = new Settings();
            defaults.Files.RecentFiles = Load().Files.RecentFiles;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, SettingsContext.Default.Settings));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Settings.ResetToDefaults", ex);
            throw;
        }
    }

    public void Save()
    {
        try
        {
            Files.BackupCount = Checked(nameof(Files.BackupCount), Files.BackupCount, DefaultBackupCount,
                v => v is >= 0 and <= DefaultBackupCountMax);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsContext.Default.Settings));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Settings.Save", ex);
        }
    }

    /// <summary>Moves a path to the top of the recent list, dropping duplicates.</summary>
    public void AddRecent(string path)
    {
        var recent = Files.RecentFiles;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        int max = Checked(nameof(General.MaxRecentFiles), General.MaxRecentFiles, DefaultMaxRecentFiles, v => v >= 1);
        if (recent.Count > max) recent.RemoveRange(max, recent.Count - max);
    }

    /// <summary>Drops one path from the recent list. The file itself is not touched.</summary>
    public void RemoveRecent(string path)
    {
        Files.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    // Sections of settings.json. Section classes are nested so their defaults can use the constants above.
    public GeneralSection General { get; set; } = new();
    public FilesSection Files { get; set; } = new();
    public LayoutSection Layout { get; set; } = new();
    public AppearanceSection Appearance { get; set; } = new();
    public CanvasSection Canvas { get; set; } = new();
    public SpritesSection Sprites { get; set; } = new();
    public PalettesSection Palettes { get; set; } = new();
    public AnimationsSection Animations { get; set; } = new();
    public SoundsSection Sounds { get; set; } = new();

    public sealed class GeneralSection
    {
        public bool SwitchTabOnOpen { get; set; } = true;
        /// <summary>Sprites, Animations and Palettes show the same sprite (leaving Animations selects the frame's sprite; the Palettes preview slider selects too).</summary>
        public bool SyncSpriteAcrossTabs { get; set; } = true;
        public bool ShowStatusBar { get; set; } = true;
        /// <summary>Theme file name (minus .txt) from the themes folder. A settings.json that already names a theme keeps it.</summary>
        public string ThemeName { get; set; } = Themes.Fallback.Name;
        /// <summary>How many undo steps are kept.</summary>
        public int UndoLimit { get; set; } = DefaultUndoLimit;
        /// <summary>Repeated edits of the same thing within this many seconds merge into one undo step.</summary>
        public double UndoMergeSeconds { get; set; } = DefaultUndoMergeSeconds;
        /// <summary>Length of File > Open Recent.</summary>
        public int MaxRecentFiles { get; set; } = DefaultMaxRecentFiles;
        /// <summary>How far a pressed project tab must move before it is dragged to a new position.</summary>
        public double TabDragThresholdPixels { get; set; } = DefaultTabDragThresholdPixels;
        public bool ScrollBarsAutoHide { get; set; } = DefaultScrollBarsAutoHide;
        /// <summary>Shortest time the splash screen shows at startup; it also stays until the main window is open.</summary>
        public int SplashMilliseconds { get; set; } = DefaultSplashMilliseconds;
        /// <summary>
        /// Lifts the group/number input ceilings (sprite table cells, the sprite
        /// panel's spinners, the onion pin) so engine limits can be probed from
        /// the UI. Values the on-disk format can't hold are truncated on save;
        /// the save paths say so in the status bar.
        /// </summary>
        public bool DebugMode { get; set; }
    }

    public sealed class FilesSection
    {
        public List<string> RecentFiles { get; set; } = [];
        /// <summary>Folder the last Open-style picker ended in.</summary>
        public string? LastDirectory { get; set; }
        /// <summary>Folder the last Save/Export picker ended in (falls back to LastDirectory).</summary>
        public string? LastSaveDirectory { get; set; }
        /// <summary>Number of prior file versions to keep when saving (0 disables backups).</summary>
        public int BackupCount { get; set; } = DefaultBackupCount;
        /// <summary>Minutes between crash-recovery snapshots.</summary>
        public int RecoveryIntervalMinutes { get; set; } = DefaultRecoveryIntervalMinutes;
        /// <summary>Save Like SprMaker: the tool last picked (true = SprMaker 2.01, SFFv2).</summary>
        public bool SprMakerUseV2 { get; set; } = true;
        /// <summary>Save Like SprMaker: last options per tool (Reset restores the tools' defaults).</summary>
        public Sff.SprMakerOptions? SprMaker { get; set; }
        public Sff.SprMaker2Options? SprMaker2 { get; set; }
    }

    public sealed class LayoutSection
    {
        public double WindowWidth { get; set; } = DefaultWindowWidth;
        public double WindowHeight { get; set; } = DefaultWindowHeight;
        public bool Maximized { get; set; }
        /// <summary>Floating panel windows, keyed by panel body name. Reset Panel Sizes forgets a tab's entries.</summary>
        public Dictionary<string, FloatPanelPlacement> FloatingPanels { get; set; } = new();
        // Side panel widths. Null means fit the panel to its contents on its tab's first layout.
        public double? SpritesListWidth { get; set; }
        public double? SpritesPaletteWidth { get; set; }
        public double? PalettesListWidth { get; set; }
        public double? AnimActionsWidth { get; set; }
        public double? AnimFramePanelWidth { get; set; }
        public double? SoundsListWidth { get; set; }
        public double? SoundsWaveformHeight { get; set; }
        /// <summary>Reset Panel Sizes: narrowest a fitted side panel gets, in pixels.</summary>
        public double PanelFitMinWidth { get; set; } = DefaultPanelFitMinWidth;
        /// <summary>Reset Panel Sizes: widest a fitted side panel gets, as a percentage of its tab.</summary>
        public double PanelFitMaxPercent { get; set; } = DefaultPanelFitMaxPercent;
    }

    public sealed class AppearanceSection
    {
        /// <summary>Text size, percent of the designed size (Preferences).</summary>
        public double FontScalePercent { get; set; } = DefaultFontScalePercent;
        /// <summary>Button padding, percent of the designed size (Preferences).</summary>
        public double ButtonScalePercent { get; set; } = DefaultButtonScalePercent;
        /// <summary>Range Preferences offers for both scales.</summary>
        public double UiScaleMinPercent { get; set; } = DefaultUiScaleMinPercent;
        public double UiScaleMaxPercent { get; set; } = DefaultUiScaleMaxPercent;
        /// <summary>
        /// Pixels between two buttons (Preferences). One value for every panel and tab,
        /// so the same control looks the same everywhere. Scaled by ButtonScalePercent,
        /// so it keeps up with the button size.
        /// </summary>
        public double ButtonGap { get; set; } = DefaultButtonGap;
        /// <summary>Visible thickness and resize-track width of panel separators, in pixels.</summary>
        public double PanelSeparatorWidth { get; set; } = DefaultPanelSeparatorWidth;
        /// <summary>Standard inset for panel content, in pixels (Preferences).</summary>
        public double PanelContentInset { get; set; } = DefaultPanelContentInset;
    }

    public sealed class CanvasSection
    {
        public bool ShowMask { get; set; }
        public bool FullCrosshair { get; set; } = true;
        /// <summary>Sprite canvas zoom steps (1 = actual size; Actual Size picks the step nearest 1).</summary>
        public double[] ZoomLevels { get; set; } = DefaultZoomLevels;
        /// <summary>How far the mouse must move with the Select tool before a press selects or moves instead of clearing the selection.</summary>
        public double SelectionDragThresholdPixels { get; set; } = DefaultSelectionDragThresholdPixels;
        /// <summary>Flat, Checkerboard or Image; null = not chosen yet (Checkerboard).</summary>
        public string? BackgroundStyle { get; set; }
        /// <summary>#RRGGBB canvas base color; null = the theme's.</summary>
        public string? BaseColor { get; set; }
        /// <summary>#RRGGBB second checker square; null = auto from the base.</summary>
        public string? CheckerColor { get; set; }
        /// <summary>Transparency checkerboard square size, in screen pixels. The checkerboard
        /// is drawn in screen space, so this does not change as the canvas zooms.</summary>
        public int CheckerSize { get; set; } = DefaultCheckerSize;
        public string? BackgroundImagePath { get; set; }
        /// <summary>Tiles the background image across the canvas instead of drawing it once, centered on the axis.</summary>
        public bool BackgroundTile { get; set; }
        /// <summary>Moves the background right/down, in pixels at 100% zoom.</summary>
        public int BackgroundOffsetX { get; set; }
        public int BackgroundOffsetY { get; set; }
        public double BackgroundOpacityPercent { get; set; } = DefaultBackgroundOpacityPercent;
        /// <summary>Background scales with zoom / moves with pan.</summary>
        public bool BackgroundFollowZoom { get; set; } = true;
        public bool BackgroundFollowPan { get; set; } = true;
        /// <summary>Background offset range, -this to +this.</summary>
        public int BackgroundOffsetMax { get; set; } = DefaultBackgroundOffsetMax;
        /// <summary>Lines or Image (Image with no usable image falls back to Lines).</summary>
        public string CrosshairMode { get; set; } = "Lines";
        /// <summary>#RRGGBB.</summary>
        public string CrosshairColor { get; set; } = "#E0E0E0";
        public double CrosshairArmLength { get; set; } = DefaultCrosshairArmLength;
        public double CrosshairThickness { get; set; } = DefaultCrosshairThickness;
        public string? CrosshairImagePath { get; set; }
        /// <summary>Crosshair image pixel placed on the axis.</summary>
        public int CrosshairHotspotX { get; set; }
        public int CrosshairHotspotY { get; set; }
        public bool CrosshairScaleWithZoom { get; set; }
        public double CrosshairArmMin { get; set; } = DefaultCrosshairArmMin;
        public double CrosshairArmMax { get; set; } = DefaultCrosshairArmMax;
        public double CrosshairThicknessMin { get; set; } = DefaultCrosshairThicknessMin;
        public double CrosshairThicknessMax { get; set; } = DefaultCrosshairThicknessMax;
    }

    public sealed class SpritesSection
    {
        public bool OnionSkin { get; set; }
        public bool OnionGrayscale { get; set; } = true;
        /// <summary>Sort Ascending on the whole file puts group 9000 first, so the portraits the
        /// select screen needs are read from the start of the file.</summary>
        public bool SortPortraitsFirst { get; set; } = true;
        /// <summary>Largest paint-brush square, in pixels per side.</summary>
        public int MaxBrushSize { get; set; } = 32;
        /// <summary>Find Similar Sprites' three levels, as the minimum percentages of visible
        /// pixel positions whose pixel data match. The dialog offers these as named choices rather
        /// than a slider, so these are the numbers behind the names. Nothing below the loosest is
        /// offered because permissive scans are slower. See "Find Similar Sprites cost and score
        /// floor" in agents/features.md.</summary>
        public int SimilarExactPercent { get; set; } = DefaultSimilarExact;
        /// <summary>The "Almost identical" level.</summary>
        public int SimilarNearPercent { get; set; } = DefaultSimilarNear;
        /// <summary>The "Similar" level, the most permissive one offered.</summary>
        public int SimilarLoosePercent { get; set; } = DefaultSimilarLoose;
        /// <summary>Sprite Resize dialog's initial percentage and allowed percentage range.</summary>
        public int SpriteScalePercent { get; set; } = DefaultSpriteScalePercent;
        public int SpriteScaleMinPercent { get; set; } = DefaultSpriteScaleMinPercent;
        public int SpriteScaleMaxPercent { get; set; } = DefaultSpriteScaleMaxPercent;
        /// <summary>Color pairs the Sprites tab's color replacer offers (1-64). A high value makes one
        /// long row that scrolls sideways instead of wrapping, so the user trades a cluttered panel for
        /// the pairs; nonsense values are clamped and logged.</summary>
        public int ColorReplacePairs { get; set; } = DefaultColorReplacePairs;
        /// <summary>Frame and sprite table fill handles: while dragged past the table's edge, one row scrolls per this many ms.</summary>
        public double FillScrollMilliseconds { get; set; } = DefaultFillScrollMilliseconds;
        /// <summary>Onion-skin overlay opacity, 0-1 (also the Onion Skin panel's slider).</summary>
        public double OnionOpacity { get; set; } = DefaultOnionOpacity;
        /// <summary>Sprite Tools swatch cell size that Reset Panel Sizes fits the panel to (16 = 256px grid).</summary>
        public double SpriteSwatchCellSize { get; set; } = DefaultSpriteSwatchCellSize;
        public int PaintUndoLimit { get; set; } = DefaultPaintUndoLimit;
        /// <summary>Alpha below which an imported pixel is treated as transparent (index 0).</summary>
        public int NearestColorAlphaThreshold { get; set; } = DefaultNearestColorAlphaThreshold;
        /// <summary>Sprite sheet import: sprites smaller than this on their longer side are dropped
        /// rather than imported, since a sheet's stray pixels are indistinguishable from small
        /// sprites. 0 keeps them all.</summary>
        public int SheetMinimumSizePixels { get; set; } = DefaultSheetMinimumSizePixels;
    }

    public sealed class PalettesSection
    {
        /// <summary>Palette swatch zoom limits, in pixels per cell.</summary>
        public double PaletteCellMin { get; set; } = DefaultPaletteCellMin;
        public double PaletteCellMax { get; set; } = DefaultPaletteCellMax;
        /// <summary>Export Palette as .png: pixels per swatch in the 16×16 grid.</summary>
        public int PalettePngSwatchSize { get; set; } = DefaultPalettePngSwatchSize;
        /// <summary>True keeps perceived lightness (OKLCH) as hue moves; false uses HSV, where brightness shifts with hue.</summary>
        public bool PalettePerceptualHue { get; set; } = DefaultPalettePerceptualHue;
        /// <summary>Color picker box size in pixels (hue runs down the height).</summary>
        public int ColorPickerBoxWidth { get; set; } = DefaultColorPickerBoxWidth;
        public int ColorPickerBoxHeight { get; set; } = DefaultColorPickerBoxHeight;
    }

    public sealed class AnimationsSection
    {
        public bool AnimOnionSkin { get; set; }
        public bool AnimOnionGrayscale { get; set; } = true;
        public bool AnimOnionClsn { get; set; } = true;
        /// <summary>Seconds playback waits on an infinite (-1) frame before restarting the preview.</summary>
        public double InfiniteAnimationTimeout { get; set; } = 1.0;
        /// <summary>The most boxes Auto Clsn2 will generate in one go.</summary>
        public int MaxAutoBoxes { get; set; } = 100;
        /// <summary>Auto Clsn2 dialog's box-count pre-fill (the dialog then remembers the last choice until restart).</summary>
        public int AutoBoxesDefault { get; set; } = DefaultAutoBoxes;
        /// <summary>Auto Clsn2 dialog's layout pre-fill: Stacked, SideBySide or Auto.</summary>
        public string AutoBoxesLayout { get; set; } = "Stacked";
        /// <summary>Auto Clsn: a 32-bit pixel with alpha below this (1-255) counts as transparent.</summary>
        public int AutoBoxesAlphaThreshold { get; set; } = DefaultAutoBoxesAlphaThreshold;
        /// <summary>Deepest timeline zoom, in pixels per tick.</summary>
        public double TimelineMaxPixelsPerTick { get; set; } = DefaultTimelineMaxPixelsPerTick;
        /// <summary>Timeline zoom factor per wheel notch (above 1).</summary>
        public double TimelineZoomStep { get; set; } = DefaultTimelineZoomStep;
        /// <summary>Timeline Shift+wheel scroll per notch, as a fraction of the visible width.</summary>
        public double TimelineScrollStep { get; set; } = DefaultTimelineScrollStep;
        /// <summary>How far the mouse must move on the timeline before a press scrubs instead of jumping to the frame's start.</summary>
        public double TimelineDragThresholdPixels { get; set; } = DefaultTimelineDragThresholdPixels;
        /// <summary>Timeline tick marks show from this many pixels per tick.</summary>
        public double TimelineTickMarkMinPixels { get; set; } = DefaultTimelineTickMarkMinPixels;
        /// <summary>Animation Onion Skin opacity, 0-1 (its panel's slider).</summary>
        public double AnimOnionOpacity { get; set; } = DefaultOnionOpacity;
        /// <summary>Delay before the frame text view re-syncs the caret to a frame after typing.</summary>
        public int TextViewRespanMilliseconds { get; set; } = DefaultTextViewRespanMilliseconds;
        /// <summary>Export GIF's pre-filled scale.</summary>
        public int GifScale { get; set; } = DefaultGifScale;
        /// <summary>Highest scale Export GIF offers.</summary>
        public int GifScaleMax { get; set; } = DefaultGifScaleMax;
        /// <summary>Largest GIF Export GIF writes, in pixels a side (after scaling).</summary>
        public int GifMaxSide { get; set; } = DefaultGifMaxSide;
    }

    public sealed class SoundsSection
    {
        /// <summary>Change Pitch accepts -this to +this semitones (12 = one octave). Must be positive.</summary>
        public double PitchMaxSemitones { get; set; } = DefaultPitchMaxSemitones;
        /// <summary>Change Tempo and Change Speed accept RateMinPercent to RateMaxPercent (100 = unchanged). Needs 0 &lt; min &lt; max.</summary>
        public double RateMinPercent { get; set; } = DefaultRateMinPercent;
        public double RateMaxPercent { get; set; } = DefaultRateMaxPercent;
        /// <summary>Deepest waveform zoom, in pixels per sample.</summary>
        public double WaveformMaxPixelsPerSample { get; set; } = DefaultWaveformMaxPixelsPerSample;
        /// <summary>Waveform zoom factor per wheel notch or Zoom In/Out click (above 1).</summary>
        public double WaveformZoomStep { get; set; } = DefaultWaveformZoomStep;
        /// <summary>Waveform Shift+wheel scroll per notch, as a fraction of the visible width.</summary>
        public double WaveformScrollStep { get; set; } = DefaultWaveformScrollStep;
        /// <summary>Echo dialog pre-fills: delay in ms and decay (each repeat's level, % of the one before).</summary>
        public double EchoDelayMilliseconds { get; set; } = DefaultEchoDelayMilliseconds;
        public double EchoDecayPercent { get; set; } = DefaultEchoDecayPercent;
        /// <summary>How much Echo's Decay spinner changes per click, in percent.</summary>
        public double EchoDecayStep { get; set; } = DefaultEchoDecayStep;
        /// <summary>Most repeats Echo adds, Auto or typed, so the tail can't go on for ever.</summary>
        public int EchoMaxRepeats { get; set; } = DefaultEchoMaxRepeats;
        /// <summary>Fade In/Fade Out dialog pre-fill, in ms.</summary>
        public double FadeMilliseconds { get; set; } = DefaultFadeMilliseconds;
        /// <summary>Crossfade where an edit on a waveform selection meets untouched audio, in ms (0 = hard edges). Not used by fades.</summary>
        public double SelectionCrossfadeMilliseconds { get; set; } = DefaultSelectionCrossfadeMilliseconds;
        /// <summary>How far the mouse must move on the waveform before a press counts as a drag (selection) rather than a click.</summary>
        public double WaveformDragThresholdPixels { get; set; } = DefaultWaveformDragThresholdPixels;
        /// <summary>How long the waveform playhead waits before moving, in ms, to match the delay before a sound is heard (0-2000).</summary>
        public double PlaybackLatencyMilliseconds { get; set; } = DefaultPlaybackLatencyMilliseconds;
        /// <summary>Increase Loudness dialog pre-fill (1-100).</summary>
        public double LoudnessDefaultAmount { get; set; } = Snd.SndEdit.DefaultLoudnessAmount;
        /// <summary>Change Volume's upper limit, in percent.</summary>
        public double VolumeMaxPercent { get; set; } = DefaultVolumeMaxPercent;
        /// <summary>Downsample All dialog pre-fill, in Hz.</summary>
        public int DownsampleAllRate { get; set; } = DefaultDownsampleAllRate;
        /// <summary>Insert Silence's pre-filled length, in ms.</summary>
        public double InsertSilenceMilliseconds { get; set; } = DefaultInsertSilenceMilliseconds;
        /// <summary>Longest silence Insert Silence adds per side, in ms.</summary>
        public double InsertSilenceMaxMilliseconds { get; set; } = DefaultInsertSilenceMaxMilliseconds;
        /// <summary>Amplitude below which Trim Silence counts a sample as silent, on the -1..1 scale. 0.01 = -40 dBFS, exactly the 1% point.</summary>
        public double SilenceThreshold { get; set; } = 0.01;
    }
}
