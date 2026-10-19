using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sff;

namespace Editor;

public partial class MainWindow
{
    // ---- palette editing --------------------------------------------------

    private bool CanEditPaletteAlpha(Palette pal) =>
        ReferenceEquals(pal, _scratchPalette)
        || pal.Source == PaletteSource.External
        || _file is null
        || !SffVersions.IsV1(_file.Version);

    /// <summary>
    /// Repaints the palette swatch views and, if this is the palette currently
    /// in use, rebuilds the canvases that bake it into a bitmap at Show()-time
    /// (so a color change needs more than just a repaint there). Shared by
    /// every place that edits palette colors in bulk or one at a time.
    /// </summary>
    private void RefreshPaletteViews(Palette pal)
    {
        if (_standalonePalettes.Contains(pal))
            pal.Dirty = _undo.Differs(StandalonePaletteKindFor(pal));
        else if (pal.Source == PaletteSource.External && _file?.Palettes.Contains(pal) == true)
            pal.Dirty = true;
        RebuildPaletteList();
        Swatches.InvalidateVisual();
        BigSwatches.InvalidateVisual();
        if (ReferenceEquals(pal, ActivePalette()))
        {
            SpriteCanvas.Show(_current, pal, _showMask);
            PalettePreview.Show(PalettePreviewSprite(), pal, _showMask);
        }
    }

    /// <summary>
    /// Edits one entry of whichever palette was last clicked. Every sprite
    /// using a real palette changes at once, which is the point of a shared
    /// color table; a scratch edit affects nothing but the scratch grid.
    /// </summary>
    private void OnColorChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || _editingPalette is not { } pal || _selectedSwatch < 0) return;
        if (_selectedSwatch >= pal.Colors.Length) return;

        int index = _selectedSwatch;
        var before = pal.Colors[index];
        var after = new Rgba(
            (byte)RSlider.Value, (byte)GSlider.Value,
            (byte)BSlider.Value, (byte)ASlider.Value);
        if (before.Equals(after)) return;

        EnsureColorSession();
        pal.Colors[index] = after;
        if (ReferenceEquals(pal, _scratchPalette)) ScratchSwatches.InvalidateVisual();
        else RefreshPaletteViews(pal);
        RebaseAfterColorOp();
    }

    // ---- color picker drop-down --------------------------------------------

    private Palette? _colorPickPalette;
    private int _colorPickIndex = -1;
    /// <summary>Color when the picker opened; the live preview shows against it, and OK or Cancel resolves against it.</summary>
    private Rgba _colorPickBefore;
    private Rgba? _colorPickPending;
    private bool _colorPickScheduled;
    private bool _colorPickAlphaEditable;

    private void OnPickColor(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_editingPalette is not { } pal || _selectedSwatch < 0 || _selectedSwatch >= pal.Colors.Length)
            {
                StatusText.Text = "Click a color first.";
                return;
            }
            _colorPickPalette = pal;
            _colorPickIndex = _selectedSwatch;
            _colorPickBefore = pal.Colors[_colorPickIndex];
            _colorPickAlphaEditable = CanEditPaletteAlpha(pal);
            // Captured here, not when the picker closes: the picked color previews straight
            // into the palette while the flyout is open, so by OK the old color is already gone.
            EnsureColorSession();
            var c = _colorPickBefore;
            ColorPickerFlyout.Open(PickColorButton, Color.FromArgb(c.A, c.R, c.G, c.B), alpha: _colorPickAlphaEditable,
                OnColorPickerMoved, CommitColorPick, CancelColorPick);
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnPickColor", ex);
        }
    }

    private void OnColorPickerMoved(Color c)
    {
        _colorPickPending = new Rgba(c.R, c.G, c.B,
            _colorPickAlphaEditable ? c.A : _colorPickBefore.A);
        if (_colorPickScheduled) return;
        _colorPickScheduled = true;
        Dispatcher.UIThread.Post(FlushColorPick, DispatcherPriority.Background); // moves between redraws collapse into one
    }

    /// <summary>Shows the latest picked color live, without an undo step (Commit and Reset handle the session).</summary>
    private void FlushColorPick()
    {
        _colorPickScheduled = false;
        try
        {
            if (_colorPickPending is not { } c || _colorPickPalette is not { } pal || _colorPickIndex >= pal.Colors.Length) return;
            _colorPickPending = null;
            pal.Colors[_colorPickIndex] = c;
            if (ReferenceEquals(pal, _scratchPalette)) ScratchSwatches.InvalidateVisual();
            else RefreshPaletteViews(pal);
            if (ReferenceEquals(pal, _editingPalette) && _selectedSwatch == _colorPickIndex)
            {
                _suppressEvents = true;
                RSlider.Value = c.R; GSlider.Value = c.G; BSlider.Value = c.B; ASlider.Value = c.A;
                _suppressEvents = false;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("FlushColorPick", ex);
        }
    }

    /// <summary>OK: one undo step from the color the picker opened with.</summary>
    private void CommitColorPick()
    {
        try
        {
            FlushColorPick();
            if (_colorPickPalette is not { } pal || _colorPickIndex < 0 || _colorPickIndex >= pal.Colors.Length) return;
            _colorPickPalette = null;
            var before = _colorPickBefore;
            var after = pal.Colors[_colorPickIndex];
            if (before.Equals(after)) return;
            // No EnsureColorSession here: FlushColorPick already wrote the picked color
            // into the palette (OnColorPickerMoved previews it live), so the baseline has
            // to have been captured when the flyout opened. See OnPickColor.
            RebaseAfterColorOp();
        }
        catch (Exception ex)
        {
            CrashLog.Write("CommitColorPick", ex);
        }
    }

    /// <summary>Cancel: puts back the color the picker opened with; no undo step.</summary>
    private void CancelColorPick()
    {
        try
        {
            _colorPickPending = null;
            if (_colorPickPalette is not { } pal || _colorPickIndex < 0 || _colorPickIndex >= pal.Colors.Length) return;
            _colorPickPending = _colorPickBefore;
            FlushColorPick(); // same live path, so views and boxes follow
            _colorPickPalette = null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CancelColorPick", ex);
        }
    }

    /// <summary>
    /// Applies a simple per-color operation (Invert, Grayscale) to whatever's selected in the
    /// last-clicked palette. Like every other operation in this panel it writes straight through
    /// and leaves Commit/Reset to deal with it, so several can be stacked; the RGBA boxes are
    /// refreshed here because the swatch being edited is usually one of the ones touched.
    /// </summary>
    private void ApplyColorOp(Func<Rgba, Rgba> op)
    {
        if (_editingPalette is not { } pal || _editingView is not { } view) return;
        List<int> indices = view.Selection.Count > 0 ? view.Selection.ToList()
            : _selectedSwatch >= 0 ? new List<int> { _selectedSwatch } : new List<int>();
        indices = indices.Where(i => i < pal.Colors.Length).ToList();
        if (indices.Count == 0) { StatusText.Text = "Click a color first."; return; }

        EnsureColorSession();
        foreach (int i in indices) pal.Colors[i] = op(pal.Colors[i]);

        if (ReferenceEquals(pal, _scratchPalette)) view.InvalidateVisual();
        else RefreshPaletteViews(pal);
        if (_selectedSwatch >= 0 && _selectedSwatch < pal.Colors.Length && indices.Contains(_selectedSwatch))
        {
            _suppressEvents = true;
            var c = pal.Colors[_selectedSwatch];
            RSlider.Value = c.R; GSlider.Value = c.G; BSlider.Value = c.B; ASlider.Value = c.A;
            _suppressEvents = false;
        }
        RebaseAfterColorOp();
    }

    private void OnInvertColor(object? sender, RoutedEventArgs e) =>
        ApplyColorOp(c => new Rgba((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B), c.A));

    private void OnGrayscaleColor(object? sender, RoutedEventArgs e) =>
        ApplyColorOp(c =>
        {
            byte g = (byte)Math.Clamp((int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B), 0, 255);
            return new Rgba(g, g, g, c.A);
        });

    // ---- color adjustment sliders -----------------------------------------

    /// <summary>What the sliders compute from: the color each swatch had when the last slider frame or operation set it. Zeroed and recaptured by both operations below.</summary>
    private Dictionary<int, Rgba>? _adjustBase;

    /// <summary>
    /// The colors as they were when this pending session began, before any slider moved or any
    /// operation ran. Commit saves the difference between this and what is on screen now;
    /// Reset puts this back. Deliberately NOT moved by an operation, so a session can stack
    /// slider moves with Invert, the color picker, the eyedropper and the rest and still be
    /// undone in one step.
    /// </summary>
    private Dictionary<int, Rgba>? _adjustSession;

    /// <summary>Which swatches a pending change covers: the grid's selection, else the one selected swatch.</summary>
    private List<int> AdjustIndices()
    {
        if (_editingPalette is not { } pal) return [];
        List<int> indices = _editingView is { } view && view.Selection.Count > 0 ? view.Selection.ToList()
            : _selectedSwatch >= 0 ? [_selectedSwatch] : [];
        return indices.Where(i => i < pal.Colors.Length).ToList();
    }

    private void ResetSlidersToZero()
    {
        if (LightenSlider is not null) LightenSlider.Value = 0;
        if (HueSlider is not null) HueSlider.Value = 0;
        if (RAddSlider is not null) RAddSlider.Value = 0;
        if (GAddSlider is not null) GAddSlider.Value = 0;
        if (BAddSlider is not null) BAddSlider.Value = 0;
        if (SaturationSlider is not null) SaturationSlider.Value = 0;
    }

    private void CaptureAdjustBase()
    {
        if (_editingPalette is not { } pal) { _adjustBase = null; return; }
        List<int> indices = AdjustIndices();
        _adjustBase = indices.Count == 0 ? null : indices.ToDictionary(i => i, i => pal.Colors[i]);
    }

    /// <summary>
    /// Starts a new pending session: sliders to zero, and both the slider origin and the
    /// Commit/Reset baseline captured from what is on screen now. Called when the selection
    /// changes and after a commit or a reset.
    /// </summary>
    private void ResetColorAdjustBase()
    {
        _suppressEvents = true;
        ResetSlidersToZero();
        CaptureAdjustBase();
        _adjustSession = _adjustBase is null ? null : new Dictionary<int, Rgba>(_adjustBase);
        _suppressEvents = false;
        SyncAdjustCommitButton();
    }

    /// <summary>
    /// Call after an operation has written straight into the palette — Invert, Grayscale, the
    /// RGBA boxes, the color picker, the eyedropper, Sort Colors, a gradient. The sliders
    /// recompute from <see cref="_adjustBase"/> on every move, so that has to move to the color
    /// the operation produced or the next slider move would wipe it out. The session baseline
    /// stays where it was, so Reset still reaches back past the operation too.
    /// </summary>
    private void RebaseAfterColorOp()
    {
        _suppressEvents = true;
        ResetSlidersToZero();
        CaptureAdjustBase();
        _suppressEvents = false;
        SyncAdjustCommitButton();
    }

    /// <summary>
    /// Call before an operation writes to the palette, so a session always exists for it to
    /// commit into or be reset from. Does nothing once one is under way, which is what lets
    /// operations stack instead of each starting a fresh baseline.
    /// </summary>
    private void EnsureColorSession()
    {
        if (_adjustSession is { Count: > 0 }) return;
        if (_editingPalette is not { } pal) return;
        List<int> indices = AdjustIndices();
        _adjustSession = indices.Count == 0 ? null : indices.ToDictionary(i => i, i => pal.Colors[i]);
    }

    /// <summary>
    /// Turns the pending session into one undoable edit: Commit saves whatever is on screen
    /// now, against the colors the session started from. Nothing is replayed — every operation
    /// already wrote itself into the palette as it happened. Leaving what the panel is editing,
    /// in any way, discards instead: see DiscardPendingColorAdjust and its call sites.
    /// </summary>
    private void CommitPendingColorAdjust()
    {
        if (_adjustSession is not { Count: > 0 } session || _editingPalette is not { } pal) { SyncAdjustCommitButton(); return; }
        // Scratch is never undoable: its colors are already applied, and the caller's ResetColorAdjustBase keeps them.
        if (ReferenceEquals(pal, _scratchPalette)) { SyncAdjustCommitButton(); return; }
        bool changed = session.Any(kv => kv.Key < pal.Colors.Length && !pal.Colors[kv.Key].Equals(kv.Value));
        if (!changed) { SyncAdjustCommitButton(); return; }

        var before = new Dictionary<int, Rgba>(session);
        var after = before.Keys.Where(i => i < pal.Colors.Length).ToDictionary(i => i, i => pal.Colors[i]);
        void Apply(Dictionary<int, Rgba> block)
        {
            foreach (var (i, c) in block) if (i < pal.Colors.Length) pal.Colors[i] = c;
            RefreshPaletteViews(pal);
        }
        Edit("color edits", () => Apply(after), () => Apply(before), kind: _standalonePalettes.Contains(pal) ? StandalonePaletteKindFor(pal) : SffKind);
        SyncAdjustCommitButton();
    }

    /// <summary>Puts every previewed swatch back to where the session started — the "Discard" side of the pending session, and what the Reset button uses too. No undo entry, since nothing permanent happened.</summary>
    private void DiscardPendingColorAdjust()
    {
        if (_adjustSession is not { Count: > 0 } session || _editingPalette is not { } pal) return;
        foreach (var (i, c) in session) if (i < pal.Colors.Length) pal.Colors[i] = c;

        if (ReferenceEquals(pal, _scratchPalette)) _editingView?.InvalidateVisual();
        else RefreshPaletteViews(pal);

        if (_selectedSwatch >= 0 && session.TryGetValue(_selectedSwatch, out var sc))
        {
            _suppressEvents = true;
            RSlider.Value = sc.R; GSlider.Value = sc.G; BSlider.Value = sc.B; ASlider.Value = sc.A;
            _suppressEvents = false;
        }
        SyncAdjustCommitButton(); // colors are back at baseline, so this always disables it — belt-and-braces, callers don't all re-sync afterward themselves
    }

    /// <summary>True when the live colors differ from where the session started — which is every operation as well as every slider. Leaves out the scratch palette, which is never saved, so it never triggers an uncommitted-adjustment prompt.</summary>
    private bool HasPendingColorAdjust() =>
        _adjustSession is { Count: > 0 } && _editingPalette is { } pal && !ReferenceEquals(pal, _scratchPalette)
        && HasAdjustedColors();

    /// <summary>Any palette's preview differs from where its session started, scratch included.</summary>
    private bool HasAdjustedColors() =>
        _adjustSession is { Count: > 0 } session && _editingPalette is { } pal
        && session.Any(kv => kv.Key < pal.Colors.Length && !pal.Colors[kv.Key].Equals(kv.Value));

    private bool HasDirtyStandalonePalettes() => _standalonePalettes.Any(p => p.Dirty);

    private bool HasDirtyExternalPalettes() => _file?.Palettes.Any(p => p.Source == PaletteSource.External && p.Dirty) == true;

    /// <summary>Asks to commit or discard live slider edits before another palette takes the editor; false on Cancel.</summary>
    private async Task<bool> SettlePendingColorAdjust()
    {
        if (!HasPendingColorAdjust()) return true;
        var pending = await Dialogs.AskAboutPendingColorAdjust(this);
        if (pending == PendingAdjustChoice.Cancel) return false;
        if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
        else DiscardPendingColorAdjust();
        return true;
    }

    private async Task<bool> ResolveStandalonePalettesBeforeOpen()
    {
        if (_file is not null || _standalonePalettes.Count == 0) return true;
        if (!await SettlePendingColorAdjust()) return false;
        var dirty = _standalonePalettes.Where(p => p.Dirty).ToList();
        if (dirty.Count > 0)
        {
            var choice = await Dialogs.PromptUnsavedStandalonePalettes(this, dirty.Count);
            if (choice == StandalonePaletteOpenChoice.Cancel) return false;
            if (choice == StandalonePaletteOpenChoice.Save)
            {
                foreach (var palette in dirty)
                    if (!await SavePaletteDocument(palette)) return false;
            }
        }
        ClearStandalonePalettes();
        return true;
    }

    private async Task<bool> ConfirmStandalonePalettesForSff()
    {
        if (_file is not null || _standalonePalettes.Count == 0) return true;
        if (HasPendingColorAdjust())
        {
            var pending = await Dialogs.AskAboutPendingColorAdjust(this);
            if (pending == PendingAdjustChoice.Cancel) return false;
            if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
            else DiscardPendingColorAdjust();
        }
        var choice = await Dialogs.PromptKeepStandalonePalettes(this, _standalonePalettes.Count,
            _standalonePalettes.Count(p => p.Dirty));
        if (choice is null) return false;
        if (choice == StandalonePaletteChoice.Keep)
        {
            _standalonePalettesPendingSff.Clear();
            _standalonePalettesPendingSff.AddRange(_standalonePalettes);
            ClearStandalonePalettes();
        }
        else ClearStandalonePalettes();
        return true;
    }

    private void TransferStandalonePalettesToSff()
    {
        if (_file is null) return;
        var pending = _standalonePalettesPendingSff;
        if (pending.Count > 0)
        {
            var taken = _file.Palettes.Where(p => p.Group == 1).Select(p => p.Index).ToHashSet();
            foreach (var palette in pending)
            {
                palette.Group = 1;
                palette.Index = Enumerable.Range(1, int.MaxValue - 1).First(i => !taken.Contains(i));
                taken.Add(palette.Index);
                palette.Source = PaletteSource.External;
                _file.Palettes.Add(palette);
            }
            CrashLog.Trace($"[palette] kept {pending.Count} standalone palette file(s) in the SFF session");
            _standalonePalettesPendingSff.Clear();
            _standalonePalettes.Clear();
            _standalonePaletteOverride = -1;
        }
    }

    private void RestorePendingStandalonePalettes()
    {
        if (_file is not null || _standalonePalettesPendingSff.Count == 0) return;
        _standalonePalettes.AddRange(_standalonePalettesPendingSff);
        _standalonePalettesPendingSff.Clear();
        _standalonePaletteOverride = _standalonePalettes.Count - 1;
        RebuildPaletteList();
        RefreshPaletteTab();
        UpdateTitle();
    }

    private void ClearStandalonePalettes()
    {
        foreach (var palette in _standalonePalettes)
            _undo.MarkSaved(StandalonePaletteKindFor(palette));
        _standalonePalettes.Clear();
        _standalonePaletteOverride = -1;
        _editingPalette = null;
        _editingView = null;
        _selectedSwatch = -1;
        ColorEditor.IsEnabled = false;
        ASlider.IsEnabled = false;
    }

    /// <summary>Scratch counts here: Commit keeps its preview (with no undo entry), while clicking elsewhere still discards it.</summary>
    private void SyncAdjustCommitButton()
    {
        if (AdjustCommitButton is not null) AdjustCommitButton.IsEnabled = HasAdjustedColors();
    }

    /// <summary>Saturation of one color as 0-100, in the same model AdjustColor uses.</summary>
    private static double SaturationPercent(Rgba c, bool perceptual) =>
        perceptual ? SaturationPercentOklch(c) : SaturationPercentHsv(c);

    /// <summary>HSV saturation of one color, as 0-100.</summary>
    private static double SaturationPercentHsv(Rgba c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        double min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        return max == 0 ? 0 : (max - min) / max * 100;
    }

    /// <summary>Live preview as a slider moves — writes straight into the palette but records no undo of its own; Commit Adjustments (or moving on) is what makes it permanent.</summary>
    private void OnColorAdjustChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || _adjustBase is not { Count: > 0 } based || _editingPalette is not { } pal) return;
        double lighten = LightenSlider.Value, hue = HueSlider.Value;
        // Relative, like every other slider here: a delta in percentage points off
        // each swatch's OWN starting saturation, not one shared absolute target —
        // an absolute target collapsed every selected swatch toward the same
        // number the instant you touched the slider, which is why a 1-unit move
        // could look drastic for a swatch whose own saturation was far from
        // wherever the rest of the selection happened to sit.
        double saturationDelta = SaturationSlider.Value;
        double rAdd = RAddSlider.Value, gAdd = GAddSlider.Value, bAdd = BAddSlider.Value;
        bool perceptual = _settings.PalettePerceptualHue;

        foreach (var (i, baseColor) in based)
        {
            if (i >= pal.Colors.Length) continue;
            double saturation = Math.Clamp(SaturationPercent(baseColor, perceptual) + saturationDelta, 0, 100);
            pal.Colors[i] = AdjustColor(baseColor, lighten, hue, saturation, rAdd, gAdd, bAdd, perceptual);
        }

        // Scratch only redraws its own grid, but goes on like any palette: the RGBA sliders follow
        // and Commit is re-synced (an early return here kept Commit off for these sliders).
        if (ReferenceEquals(pal, _scratchPalette)) _editingView?.InvalidateVisual();
        else RefreshPaletteViews(pal);
        if (_selectedSwatch >= 0 && based.ContainsKey(_selectedSwatch) && _selectedSwatch < pal.Colors.Length)
        {
            _suppressEvents = true;
            var c = pal.Colors[_selectedSwatch];
            RSlider.Value = c.R; GSlider.Value = c.G; BSlider.Value = c.B; ASlider.Value = c.A;
            _suppressEvents = false;
        }
        // The actual bug: this handler never told the button its state might
        // have just changed. Every OTHER exit point in this file (Commit,
        // Discard's callers via ResetColorAdjustBase, etc.) re-syncs it, but
        // the one place that fires on every slider move — this one — didn't,
        // so the button stayed however it was left after the last selection
        // change: disabled, permanently, no matter how far you dragged a slider.
        SyncAdjustCommitButton();
    }

    private void OnCommitColorAdjust(object? sender, RoutedEventArgs e)
    {
        CommitPendingColorAdjust();
        ResetColorAdjustBase();
    }

    /// <summary>Discards the live preview (puts every previewed swatch back to its pre-slider color) and zeroes the sliders — unlike Commit, nothing here is recorded on the undo stack, since nothing permanent happened.</summary>
    private void OnResetColorAdjust(object? sender, RoutedEventArgs e)
    {
        DiscardPendingColorAdjust();
        ResetColorAdjustBase();
    }

    // ---- reference image / color picker -----------------------------------

    private async void OnOpenScratchImage(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Open Reference Image",
                AllowMultiple = false,
                FileTypeFilter = [ImageType],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

            try
            {
                var (bgra, width, height) = ImageFile.LoadBgra(path);
                ScratchImage.Load(bgra, width, height);
                ShowReferenceImageSection();
                StatusText.Text = $"Loaded {Path.GetFileName(path)} — click it to set the selected swatch to that color.";
            }
            catch (Exception ex)
            {
                CrashLog.Write("OnOpenScratchImage", ex);
                await Dialogs.ShowMessage(this, "Could Not Open Image", ex.Message);
            }
        }
        catch (Exception ex)
        {
            await Failed("OnOpenScratchImage", ex);
        }
    }

    private const double DefaultReferenceHeight = 180;

    /// <summary>Unloads the reference image and collapses its area to the header strip.</summary>
    private void OnCloseScratchImage(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceImageBody.IsVisible) return;
        _preCollapseWidths[ReferenceImageSection] = PaletteRightBody.RowDefinitions[2].Height;
        PaletteRightBody.RowDefinitions[2].Height = GridLength.Auto;
        ReferenceImageBody.IsVisible = false;
        ReferenceSplitter.IsEnabled = false;
        CloseReferenceButton.IsEnabled = false;
        ScratchImage.Clear();
        StatusText.Text = "Reference image closed.";
    }

    /// <summary>Expands the reference image area to its last height this session (or the default).</summary>
    private void ShowReferenceImageSection()
    {
        if (ReferenceImageBody.IsVisible) return;
        ReferenceImageBody.IsVisible = true;
        ReferenceSplitter.IsEnabled = true;
        CloseReferenceButton.IsEnabled = true;
        PaletteRightBody.RowDefinitions[2].Height =
            _preCollapseWidths.TryGetValue(ReferenceImageSection, out var h) ? h : new GridLength(DefaultReferenceHeight);
    }

    /// <summary>A click on the reference image writes straight into whichever swatch is currently selected — same undo/refresh path as typing the RGBA boxes by hand.</summary>
    private void OnScratchImageColorPicked(Rgba picked)
    {
        if (ApplyPickedColor(picked)) DisarmEyedropper();
    }

    /// <summary>
    /// Writes a color picked from anywhere — the reference image, the sprite preview, or
    /// another swatch — into the selected swatch. One path for every source, so the
    /// pending-adjust and undo behavior can't drift apart between them. False when there
    /// was nowhere to put it; a pick that lands on the color already there still counts,
    /// so an eyedropper pass doesn't leave the tool armed for no reason.
    /// </summary>
    private bool ApplyPickedColor(Rgba picked)
    {
        if (_editingPalette is not { } pal || _selectedSwatch < 0 || _selectedSwatch >= pal.Colors.Length)
        {
            StatusText.Text = "Click a color swatch first, to choose where the picked color goes.";
            return false;
        }
        int index = _selectedSwatch;
        if (pal.Colors[index].Equals(picked)) return true;

        EnsureColorSession();
        pal.Colors[index] = picked;
        if (ReferenceEquals(pal, _scratchPalette)) ScratchSwatches.InvalidateVisual();
        else RefreshPaletteViews(pal);

        _suppressEvents = true;
        RSlider.Value = picked.R; GSlider.Value = picked.G; BSlider.Value = picked.B; ASlider.Value = picked.A;
        _suppressEvents = false;

        RebaseAfterColorOp();
        return true;
    }

    /// <summary>
    /// Eyedropper mode: the next pick from a swatch, the reference image or the sprite
    /// preview copies that color into the selected swatch instead of selecting it. One-shot,
    /// so it can't be left armed and silently swallow a later click.
    /// </summary>
    private bool _eyedropperArmed;
    private bool _previewEyedropperArmed;

    private void OnToggleEyedropper(object? sender, RoutedEventArgs e)
    {
        _eyedropperArmed = EyedropperButton.IsChecked == true;
        if (_eyedropperArmed)
        {
            _previewEyedropperArmed = false;
            PreviewEyedropperButton.IsChecked = false;
        }
        // PaletteView rewrites its own selection inside the click, before EntryClicked is
        // raised, so a pick that must leave the selection alone has to stop the click from
        // making one in the first place — there is nothing the owner could put back after.
        if (BigSwatches is not null) BigSwatches.ClickSelects = !_eyedropperArmed;
        if (ScratchSwatches is not null) ScratchSwatches.ClickSelects = !_eyedropperArmed;
        // The preview is ReadOnly, where a left-click is only ever a pan. ArmedForExternalPick
        // is what turns it back into a pick; it was already the mechanism, but sat behind the
        // ReadOnly branch, so the preview could only ever be sampled from its right-click menu.
        if (PalettePreview is not null) PalettePreview.ArmedForExternalPick = _eyedropperArmed;
        StatusText.Text = _eyedropperArmed
            ? "Copy Color: click a swatch, the reference image, or the sprite preview."
            : "";
    }

    private void OnTogglePreviewEyedropper(object? sender, RoutedEventArgs e)
    {
        _previewEyedropperArmed = PreviewEyedropperButton.IsChecked == true;
        if (_previewEyedropperArmed)
        {
            _eyedropperArmed = false;
            EyedropperButton.IsChecked = false;
            if (BigSwatches is not null) BigSwatches.ClickSelects = true;
            if (ScratchSwatches is not null) ScratchSwatches.ClickSelects = true;
            if (PalettePreview is not null) PalettePreview.ArmedForExternalPick = true;
        }
        else if (!_eyedropperArmed && PalettePreview is not null)
        {
            PalettePreview.ArmedForExternalPick = false;
        }
        StatusText.Text = _previewEyedropperArmed
            ? "Select from Preview: click or right-click a palette color in the sprite preview."
            : "";
    }

    /// <summary>True while the eyedropper should intercept the next pick from a Palettes-tab source.</summary>
    private bool EyedropperWants(PaletteView view) =>
        _eyedropperArmed && (ReferenceEquals(view, BigSwatches) || ReferenceEquals(view, ScratchSwatches));

    private void DisarmEyedropper()
    {
        if (!_eyedropperArmed) return;
        _eyedropperArmed = false;
        if (EyedropperButton is not null) EyedropperButton.IsChecked = false;
        if (BigSwatches is not null) BigSwatches.ClickSelects = true;
        if (ScratchSwatches is not null) ScratchSwatches.ClickSelects = true;
        if (PalettePreview is not null) PalettePreview.ArmedForExternalPick = false;
        StatusText.Text = "";
    }

    private void DisarmPreviewEyedropper()
    {
        if (!_previewEyedropperArmed) return;
        _previewEyedropperArmed = false;
        if (PreviewEyedropperButton is not null) PreviewEyedropperButton.IsChecked = false;
        if (!_eyedropperArmed && PalettePreview is not null) PalettePreview.ArmedForExternalPick = false;
        StatusText.Text = "";
    }

    /// <summary>Applies a hue rotation and an absolute saturation target (OKLCH, or HSV when PalettePerceptualHue is off), then an overall lighten and per-channel add, each clamped to a byte.</summary>
    private static Rgba AdjustColor(Rgba baseColor, double lighten, double hue, double saturation, double rAdd, double gAdd, double bAdd, bool perceptual)
    {
        var (r, g, b) = perceptual
            ? AdjustOklch(baseColor.R, baseColor.G, baseColor.B, hue, saturation)
            : AdjustHsv(baseColor.R, baseColor.G, baseColor.B, hue, saturation);
        r += lighten + rAdd;
        g += lighten + gAdd;
        b += lighten + bAdd;
        return new Rgba(ClampByte(r), ClampByte(g), ClampByte(b), baseColor.A);
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    // ---- perceptual (OKLCH) hue and saturation ----------------------------

    private static double SrgbToLinear(double x) => x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
    }

    /// <summary>sRGB 0-255 to OKLab (Björn Ottosson's matrices). L tracks perceived lightness.</summary>
    private static (double L, double A, double B) RgbToOklab(double r, double g, double b)
    {
        double lr = SrgbToLinear(r / 255), lg = SrgbToLinear(g / 255), lb = SrgbToLinear(b / 255);
        double l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
        double m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
        double s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>OKLab to linear sRGB (unclamped, so callers can test the gamut).</summary>
    private static (double R, double G, double B) OklabToLinear(double L, double a, double b)
    {
        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;
        double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static bool InGamut(double L, double a, double b)
    {
        const double eps = 1e-6;
        var (r, g, bl) = OklabToLinear(L, a, b);
        return r >= -eps && r <= 1 + eps && g >= -eps && g <= 1 + eps && bl >= -eps && bl <= 1 + eps;
    }

    /// <summary>The largest OKLab chroma sRGB can show at this lightness and hue.</summary>
    private static double MaxChroma(double L, double hue)
    {
        double lo = 0, hi = 0.4;
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            if (InGamut(L, mid * Math.Cos(hue), mid * Math.Sin(hue))) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Rotates hue in OKLCH with lightness held, so perceived brightness doesn't move with hue.
    /// Saturation is a percent of the most chroma sRGB allows at that lightness and hue, like HSV.
    /// </summary>
    private static (double R, double G, double B) AdjustOklch(double r, double g, double b, double degrees, double saturationPercent)
    {
        var (L, a, bb) = RgbToOklab(r, g, b);
        double hue = Math.Atan2(bb, a) + degrees * Math.PI / 180;
        double chroma = Math.Clamp(saturationPercent / 100, 0, 1) * MaxChroma(L, hue);
        var (lr, lg, lb) = OklabToLinear(L, chroma * Math.Cos(hue), chroma * Math.Sin(hue));
        return (LinearToSrgb(lr) * 255, LinearToSrgb(lg) * 255, LinearToSrgb(lb) * 255);
    }

    /// <summary>OKLCH saturation of one color as 0-100: its chroma as a share of MaxChroma at its lightness and hue.</summary>
    private static double SaturationPercentOklch(Rgba c)
    {
        var (L, a, b) = RgbToOklab(c.R, c.G, c.B);
        double max = MaxChroma(L, Math.Atan2(b, a));
        return max <= 0 ? 0 : Math.Clamp(Math.Sqrt(a * a + b * b) / max * 100, 0, 100);
    }

    /// <summary>RGB (0-255 each) through HSV: rotates hue by degrees, then sets saturation directly as an absolute percent — 0 is grayscale, 100 is fully saturated, matching a normal HSL/HSV saturation slider rather than a relative nudge (a plain multiplier can never raise an already-desaturated color, which is why this isn't relative).</summary>
    private static (double R, double G, double B) AdjustHsv(double r, double g, double b, double degrees, double saturationPercent)
    {
        double rn = r / 255, gn = g / 255, bn = b / 255;
        double max = Math.Max(rn, Math.Max(gn, bn)), min = Math.Min(rn, Math.Min(gn, bn));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rn) h = 60 * (((gn - bn) / delta) % 6);
            else if (max == gn) h = 60 * (((bn - rn) / delta) + 2);
            else h = 60 * (((rn - gn) / delta) + 4);
        }
        if (h < 0) h += 360;
        double v = max;

        h = (h + degrees) % 360;
        if (h < 0) h += 360;
        double s = Math.Clamp(saturationPercent / 100, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        var (r2, g2, b2) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((r2 + m) * 255, (g2 + m) * 255, (b2 + m) * 255);
    }

    /// <summary>A box selection changed the edited grid's selection, so rebase the sliders as SelectAll does.</summary>
    private void OnBoxSelectionChanged(PaletteView view)
    {
        if (ReferenceEquals(view, _editingView)) ResetColorAdjustBase();
    }

    /// <summary>Every swatch grid reports through the same handlers, tagged with its target palette.</summary>
    private void WirePaletteViews()
    {
        Swatches.EntryClicked += i => OnSwatchClicked(ActivePalette(), Swatches, i);
        BigSwatches.EntryClicked += i => OnSwatchClicked(ActivePalette(), BigSwatches, i);
        ScratchSwatches.EntryClicked += i => OnSwatchClicked(_scratchPalette, ScratchSwatches, i);
        Swatches.BoxSelected += () => OnBoxSelectionChanged(Swatches);
        BigSwatches.BoxSelected += () => OnBoxSelectionChanged(BigSwatches);
        ScratchSwatches.BoxSelected += () => OnBoxSelectionChanged(ScratchSwatches);

        Swatches.ColorsDropped += (at, c) => DropColors(ActivePalette(), Swatches, at, c);
        BigSwatches.ColorsDropped += (at, c) => DropColors(ActivePalette(), BigSwatches, at, c);

        BigSwatches.ContextRequested += (_, e) => ShowSwatchContextMenu(BigSwatches, ActivePalette(), e);
        ScratchSwatches.ContextRequested += (_, e) => ShowSwatchContextMenu(ScratchSwatches, _scratchPalette, e);

        ScratchSwatches.Show(_scratchPalette);
        // Read-only preview: can never write to the Sprite it's showing —
        // see SpriteView.ReadOnly's own doc comment for the full guarantee.
        PalettePreview.ReadOnly = true;

        // Right-click > Eyedropper on the preview (see OnPalettePreviewContextRequested)
        // picks a color: highlight it in BigSwatches exactly like clicking it there
        // would, then run the normal swatch-click path for the RGBA fields and the
        // palette-list sync. Left-click stays panning only.
        // While the eyedropper is armed the same pick copies into the selected swatch
        // instead of selecting, which is the whole difference between the two modes.
        PalettePreview.ColorPicked += i =>
        {
            if (_eyedropperArmed)
            {
                if (ActivePalette() is { } pickPal && i >= 0 && i < pickPal.Colors.Length
                    && ApplyPickedColor(pickPal.Colors[i])) DisarmEyedropper();
                return;
            }
            BigSwatches.SelectOnly(i);
            OnSwatchClicked(ActivePalette(), BigSwatches, i);
            if (_previewEyedropperArmed) DisarmPreviewEyedropper();
        };

        // A true-color preview has no palette index to select, so the pick arrives as a color
        // and there is nothing to select — only the eyedropper has anywhere to put it.
        PalettePreview.ColorPickedValue += c =>
        {
            if (_eyedropperArmed && ApplyPickedColor(c)) DisarmEyedropper();
        };
    }

    /// <summary>
    /// Right-click on the Palettes tab's sprite preview: Eyedropper picks a
    /// color index (and its palette), and "Switch to This Sprite's Palette"
    /// selects whatever palette the PREVIEWED SPRITE actually references
    /// (Sprite.Palette) — not necessarily ActivePalette(), which may be
    /// forcing some other palette onto it for comparison. That's the point
    /// of the item: fixing it when the preview is showing the wrong one.
    /// </summary>
    private void OnPalettePreviewContextRequested(int boxIndex, Point pos)
    {
        var items = new List<(string, Action)>
        {
            ("Center Image in Canvas", PalettePreview.CenterImage),
            MenuSeparator,
            ("Select Color Here", () => PalettePreview.PickColorAt(pos)),
        };
        if (PalettePreviewSprite()?.Palette is { } pal) items.Add(("Switch to This Sprite's Palette", () => SelectPalette(pal)));
        ShowContextMenu(PalettePreview, items.ToArray());
    }

    /// <summary>
    /// Right-click on the reference image. Centering is the sprite canvas's item,
    /// kept word for word so the same gesture reads the same on both views; the
    /// color pick is a left click here, so it needs no menu entry.
    /// </summary>
    private void OnScratchImageContextRequested()
    {
        ShowContextMenu(ScratchImage, ("Center Image in Canvas", ScratchImage.CenterImage));
    }

    /// <summary>Writes dropped colors into a palette starting at the drop slot.</summary>
    private void DropColors(Palette? pal, PaletteView view, int at, Rgba[] colors)
    {
        if (pal is null) return;
        int n = Math.Min(colors.Length, pal.Colors.Length - at);
        if (n <= 0) return;

        var before = pal.Colors[at..(at + n)];
        var after = colors[..n];

        if (ReferenceEquals(pal, _scratchPalette))
        {
            Array.Copy(after, 0, pal.Colors, at, n);
            view.InvalidateVisual();
            return;
        }

        void Apply(Rgba[] block)
        {
            Array.Copy(block, 0, pal.Colors, at, block.Length);
            RefreshPaletteViews(pal);
        }
        _undo.Record("drop colors", () => Apply(before), () => Apply(after));
        Apply(after);
        if (_standalonePalettes.Contains(pal)) MarkStandalonePaletteDirty(pal);
        else MarkDirty();
        StatusText.Text = $"Placed {n} color(s) starting at index {at}.";
    }

    private void OnScratchColorsDropped(int at, Rgba[] colors) => DropColors(_scratchPalette, ScratchSwatches, at, colors);

    private void OnClearScratch(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _scratchPalette.Colors.Length; i++) _scratchPalette.Colors[i] = new Rgba(0, 0, 0, 0);
        ScratchSwatches.InvalidateVisual();
    }

    /// <summary>
    /// Fills the selected swatches with a straight-line interpolation, per
    /// checked channel, between the first and last selected swatch (by index,
    /// not click order). A single selected color is a no-op with a warning —
    /// there's nothing to interpolate between.
    /// </summary>
    private async void OnCreateGradient(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_editingPalette is not { } pal || _editingView is not { } view)
            {
                StatusText.Text = "Click a color in a palette first, then select the range to fill.";
                return;
            }
            var indices = view.Selection.OrderBy(i => i).ToList();
            if (indices.Count < 2)
            {
                await Dialogs.ShowMessage(this, "Create Gradient",
                    "Select at least two colors first — a gradient needs a start and an end.");
                return;
            }

            var channels = await Dialogs.PromptGradientChannels(this, indices.Count);
            if (channels is not { } ch) return;

            var first = pal.Colors[indices[0]];
            var last = pal.Colors[indices[^1]];
            EnsureColorSession();
            for (int k = 0; k < indices.Count; k++)
            {
                double t = indices.Count == 1 ? 0 : (double)k / (indices.Count - 1);
                var c = pal.Colors[indices[k]];
                pal.Colors[indices[k]] = new Rgba(
                    ch.HasFlag(GradientChannels.R) ? Lerp(first.R, last.R, t) : c.R,
                    ch.HasFlag(GradientChannels.G) ? Lerp(first.G, last.G, t) : c.G,
                    ch.HasFlag(GradientChannels.B) ? Lerp(first.B, last.B, t) : c.B,
                    ch.HasFlag(GradientChannels.A) ? Lerp(first.A, last.A, t) : c.A);
            }

            if (ReferenceEquals(pal, _scratchPalette)) view.InvalidateVisual();
            else RefreshPaletteViews(pal);
            RebaseAfterColorOp();
            StatusText.Text = $"Gradient applied to {indices.Count} colors.";
        }
        catch (Exception ex)
        {
            await Failed("OnCreateGradient", ex);
        }
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);

    /// <summary>
/// Empties every swatch grid except <paramref name="keep"/>. The RGBA editor and the color
/// operations all work on one grid at a time (_editingView), so two grids holding a selection
/// means one of them is showing a selection that nothing will act on.
/// </summary>
private void ClearOtherSwatchSelections(PaletteView keep)
    {
        foreach (var v in new PaletteView?[] { Swatches, BigSwatches, ScratchSwatches })
            if (v is not null && !ReferenceEquals(v, keep)) v.ClearSelection();
    }

    private void SelectAllSwatches(PaletteView view)
    {
        // One batched pass over the grid, not a toggle per swatch, matching SelectAllSprites.
        // Nothing raises EntryClicked from a programmatic change, so the pending-adjust
        // baseline is rebased here: the sliders' scope is the selection, and the selection
        // just changed under them.
        ClearOtherSwatchSelections(view);
        _suppressEvents = true;
        try { view.SelectAll(); }
        finally { _suppressEvents = false; }
        ResetColorAdjustBase();
        StatusText.Text = $"Selected all {view.Selection.Count} colors.";
    }

    private void InvertSwatchSelection(PaletteView view)
    {
        ClearOtherSwatchSelections(view);
        _suppressEvents = true;
        try { view.InvertSelection(); }
        finally { _suppressEvents = false; }
        ResetColorAdjustBase();
        StatusText.Text = $"{view.Selection.Count} colors selected.";
    }

    /// <summary>
    /// Sorts the selected colors into the selected slots, by brightness, hue, or index (Index
    /// descending reverses them). A dialog asks the key, direction, and whether sprites follow.
    /// Index 0 never moves, so it is left out.
    /// </summary>
    private async void OnSortColors()
    {
        try
        {
            if (_editingPalette is not { } pal || _editingView is not { } view)
            {
                StatusText.Text = "Click a color in a palette first, then select the colors to sort.";
                return;
            }
            var indices = ReorderSelection(view, pal);
            if (indices is null)
            {
                await Dialogs.ShowMessage(this, "Sort Colors",
                    "Select at least two colors to sort. Index 0 never moves, so it doesn't count.");
                return;
            }
            var (canUpdate, reason) = SpriteUpdateAvailability(pal);
            var choice = await Dialogs.PromptColorSort(this, canUpdate, reason);
            if (choice is not { } picked) return;

            var order = SortOrder(pal, indices, picked.Key, picked.Descending);
            string by = picked.Key switch
            {
                ColorSortKey.Brightness => "brightness",
                ColorSortKey.Hue => "hue",
                _ => "index",
            };
            await ApplyColorReorder(pal, view, indices, order, picked.UpdateSprites, $"Sorted {indices.Count} colors by {by}");
        }
        catch (Exception ex)
        {
            await Failed("OnSortColors", ex);
        }
    }

    /// <summary>
    /// Swaps the two selected colors. Index 0 is refused; a dialog asks whether sprites using the
    /// palette follow. Runs through the same reorder path as Sort Colors.
    /// </summary>
    private async void OnSwapColors()
    {
        try
        {
            if (_editingPalette is not { } pal || _editingView is not { } view)
            {
                StatusText.Text = "Click a color in a palette first, then select the two colors to swap.";
                return;
            }
            var indices = view.Selection.Where(i => i < pal.Colors.Length).OrderBy(i => i).ToList();
            if (indices.Count != 2)
            {
                await Dialogs.ShowMessage(this, "Swap Colors", "Select exactly two colors to swap.");
                return;
            }
            if (indices[0] == 0)
            {
                await Dialogs.ShowMessage(this, "Swap Colors", "Index 0 is the transparent slot and can't be swapped.");
                return;
            }

            var (canUpdate, reason) = SpriteUpdateAvailability(pal);
            var updateSprites = await Dialogs.PromptColorSwap(this, indices[0], indices[1], canUpdate, reason);
            if (updateSprites is not { } update) return;

            await ApplyColorReorder(pal, view, indices, [1, 0], update, $"Swapped colors {indices[0]} and {indices[1]}");
        }
        catch (Exception ex)
        {
            await Failed("OnSwapColors", ex);
        }
    }

    /// <summary>The selected indexes a reorder works on (index 0 excluded), or null when fewer than two.</summary>
    private static List<int>? ReorderSelection(PaletteView view, Palette pal)
    {
        var indices = view.Selection.Where(i => i > 0 && i < pal.Colors.Length).OrderBy(i => i).ToList();
        return indices.Count >= 2 ? indices : null;
    }

    /// <summary>Whether this palette's sprites can be updated, and the reason when they can't.</summary>
    private (bool Enabled, string? Reason) SpriteUpdateAvailability(Palette pal)
    {
        if (_file is null) return (false, "No SFF is open, so no sprites use this palette.");
        return _file.Sprites.Any(s => s.Palette == pal) ? (true, null) : (false, "No sprites use this palette.");
    }

    /// <summary>Positions within indices in sorted order: result[k] is the position whose color lands at indices[k].</summary>
    private static int[] SortOrder(Palette pal, List<int> indices, ColorSortKey key, bool descending)
    {
        Rgba ColorAt(int j) => pal.Colors[indices[j]];
        var positions = Enumerable.Range(0, indices.Count);
        IOrderedEnumerable<int> ordered = (key, descending) switch
        {
            (ColorSortKey.Brightness, false) => positions.OrderBy(j => Luma(ColorAt(j))),
            (ColorSortKey.Brightness, true) => positions.OrderByDescending(j => Luma(ColorAt(j))),
            (ColorSortKey.Index, false) => positions.OrderBy(j => j),
            (ColorSortKey.Index, true) => positions.OrderByDescending(j => j),
            (_, false) => positions.OrderBy(j => IsGray(ColorAt(j))).ThenBy(j => IsGray(ColorAt(j)) ? Luma(ColorAt(j)) : HueOf(ColorAt(j))),
            _ => positions.OrderBy(j => IsGray(ColorAt(j))).ThenByDescending(j => IsGray(ColorAt(j)) ? Luma(ColorAt(j)) : HueOf(ColorAt(j))),
        };
        return ordered.ToArray();
    }

    /// <summary>
    /// Moves the colors so the one at indices[order[k]] lands at indices[k]. With updateSprites,
    /// sprites using the palette get matching pixel indexes, all in one undo step.
    /// </summary>
    private async Task ApplyColorReorder(Palette pal, PaletteView view, List<int> indices, int[] order,
        bool updateSprites, string doneText)
    {
        if (order.Select((p, k) => p == k).All(same => same))
        {
            StatusText.Text = "Colors are already in that order.";
            return;
        }
        if (!updateSprites || _file is not { } file)
        {
            var source = indices.Select(i => pal.Colors[i]).ToArray();
            EnsureColorSession();
            for (int k = 0; k < indices.Count; k++) pal.Colors[indices[k]] = source[order[k]];
            if (ReferenceEquals(pal, _scratchPalette)) view.InvalidateVisual();
            else RefreshPaletteViews(pal);
            RebaseAfterColorOp();
            StatusText.Text = doneText + ".";
            return;
        }

        // Sprite pixels get their own undo step, so pending slider edits are committed first.
        CommitPendingColorAdjust();
        var map = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int k = 0; k < indices.Count; k++) map[indices[order[k]]] = (byte)indices[k];

        var before = pal.Colors.ToArray();
        var after = (Rgba[])before.Clone();
        for (int k = 0; k < indices.Count; k++) after[indices[k]] = before[indices[order[k]]];

        var skipped = new List<string>();
        var (swaps, sprites) = PlanSpritePixelSwaps(file, pal, map, skipped);
        Edit(doneText,
            () => ApplyReorderState(pal, after, swaps, forward: true),
            () => ApplyReorderState(pal, before, swaps, forward: false));
        ResetColorAdjustBase();

        StatusText.Text = $"{doneText}; updated {sprites} sprite{(sprites == 1 ? "" : "s")}"
            + (skipped.Count > 0 ? $"; skipped {skipped.Count}." : ".");
        if (skipped.Count > 0)
            await Dialogs.ShowMessage(this, "Sprites Not Updated", string.Join("\n", skipped));
    }

    /// <summary>A sprite's pixel indexes to replace, with the old array and dirty flag for undo.</summary>
    private sealed record PixelSwap(Sprite Owner, byte[] Before, bool DirtyBefore, byte[] After);

    /// <summary>
    /// Pixel swaps that keep sprites using pal looking the same after its colors move. Sprites
    /// that can't follow are added to skipped. Linked sprites follow their pixel owner.
    /// </summary>
    private static (List<PixelSwap> Swaps, int Sprites) PlanSpritePixelSwaps(SffFile file, Palette pal, byte[] map, List<string> skipped)
    {
        var swaps = new List<PixelSwap>();
        int sprites = 0;
        var owners = file.Sprites.Where(s => s.Palette == pal).Select(s => s.SharesWith ?? s).Distinct().ToList();
        foreach (var owner in owners)
        {
            var users = file.Sprites.Where(s => ReferenceEquals(s.SharesWith ?? s, owner)).ToList();
            var usingPal = users.Where(s => s.Palette == pal).ToList();
            if (users.Any(s => s.Palette != pal))
            {
                foreach (var s in usingPal)
                    skipped.Add($"Sprite {s.Group},{s.Index}: shares pixels with a sprite that uses another palette");
                continue;
            }
            if (owner.Indexed is null)
            {
                if (owner.DecodeError is not null)
                    foreach (var s in usingPal) skipped.Add($"Sprite {s.Group},{s.Index}: could not be decoded");
                continue; // true-color sprites store colors, not indexes, so a reorder never touches them
            }

            var before = owner.Indexed;
            var after = new byte[before.Length];
            for (int i = 0; i < before.Length; i++) after[i] = map[before[i]];
            swaps.Add(new PixelSwap(owner, before, owner.PixelsDirty, after));
            sprites += usingPal.Count;
        }
        return (swaps, sprites);
    }

    private void ApplyReorderState(Palette pal, Rgba[] colors, List<PixelSwap> swaps, bool forward)
    {
        Array.Copy(colors, pal.Colors, colors.Length);
        foreach (var swap in swaps)
        {
            swap.Owner.Indexed = forward ? swap.After : swap.Before;
            swap.Owner.PixelsDirty = forward || swap.DirtyBefore;
        }
        RefreshPaletteViews(pal);
    }

    /// <summary>Rec. 601 luma, the same weights as the grayscale operation.</summary>
    private static double Luma(Rgba c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static bool IsGray(Rgba c) => c.R == c.G && c.G == c.B;

    /// <summary>Hue in degrees (0-360); grays return 0 and are sorted separately.</summary>
    private static double HueOf(Rgba c)
    {
        double r = c.R, g = c.G, b = c.B;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta == 0) return 0;
        double h = max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        return h < 0 ? h + 360 : h;
    }

    private async void OnNewPalette(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null && !await SettlePendingColorAdjust()) return;
            AddTempPalette(new Palette { Colors = BlankColors(ActivePalette()?.Colors.Length ?? 256) }, "new palette");
            StatusText.Text = "Created a temp palette.";
        }
        catch (Exception ex)
        {
            await Failed("OnNewPalette", ex);
        }
    }

    private async void OnDuplicatePalette(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ActivePalette() is not { } source) return;
            // Every copy is a temp palette; it joins the SFF only when applied to sprites.
            if (_file is null && !await SettlePendingColorAdjust()) return;
            AddTempPalette(new Palette { Colors = (Rgba[])source.Colors.Clone() }, "duplicate palette");
            StatusText.Text = "Duplicated as a temp palette.";
        }
        catch (Exception ex)
        {
            await Failed("OnDuplicatePalette", ex);
        }
    }

    private async Task ConvertPaletteDepth(Palette palette)
    {
        try
        {
            if (_file is not { } file || !file.Palettes.Contains(palette)) return;
            var available = PaletteDepth.SupportedColorCounts
                .Where(colors => PaletteDepth.CanConvert(file, palette, colors)).ToArray();
            if (available.Length == 0)
            {
                await Dialogs.ShowMessage(this, "Convert Depth", "No different supported depth fits this palette's sprites.");
                return;
            }

            var colors = await Dialogs.PromptPaletteDepth(this, palette.Colors.Length, available);
            if (colors is null || !ReferenceEquals(_file, file) || !file.Palettes.Contains(palette)) return;

            if (HasPendingColorAdjust() && ReferenceEquals(_editingPalette, palette))
            {
                var pending = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pending == PendingAdjustChoice.Cancel) return;
                if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(palette)) return;
                if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            if (!PaletteDepth.CanConvert(file, palette, colors.Value))
            {
                await Dialogs.ShowMessage(this, "Convert Depth", "This palette no longer fits the selected depth.");
                return;
            }

            var before = (Rgba[])palette.Colors.Clone();
            var after = PaletteDepth.Convert(palette, colors.Value);

            void Apply(Rgba[] values)
            {
                palette.Colors = (Rgba[])values.Clone();
                if (ReferenceEquals(_editingPalette, palette) && _selectedSwatch >= values.Length)
                {
                    _editingPalette = null;
                    _editingView = null;
                    _selectedSwatch = -1;
                    ColorEditor.IsEnabled = false;
                    ASlider.IsEnabled = false;
                    ResetColorAdjustBase();
                }
            }
            Edit("convert palette depth", () => Apply(after), () => Apply(before));
            int bits = Array.IndexOf(PaletteDepth.SupportedColorCounts, colors.Value) + 1;
            StatusText.Text = $"Converted palette {PaletteLabel(palette)} to {bits}-bit indexed depth.";
        }
        catch (Exception ex)
        {
            await Failed("ConvertPaletteDepth", ex);
        }
    }

    private async void OnDeletePalette(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null)
            {
                if (_standalonePaletteOverride < 0 || _standalonePaletteOverride >= _standalonePalettes.Count) return;
                var palette = _standalonePalettes[_standalonePaletteOverride];
                if (palette.Dirty && !await Dialogs.Confirm(this, "Close Palette File",
                        $"{Path.GetFileName(palette.SourcePath) ?? "New palette"} has unsaved changes.", "Discard")) return;
                _standalonePalettes.RemoveAt(_standalonePaletteOverride);
                _standalonePaletteOverride = Math.Min(_standalonePaletteOverride, _standalonePalettes.Count - 1);
                _editingPalette = null;
                _editingView = null;
                _selectedSwatch = -1;
                ColorEditor.IsEnabled = false;
                ASlider.IsEnabled = false;
                RebuildPaletteList();
                RefreshPaletteTab();
                UpdateTitle();
                return;
            }
            if (_paletteOverride < 0 || _paletteOverride >= _file.Palettes.Count)
            {
                StatusText.Text = "Choose a palette in the list first; the sprite's own cannot be deleted from here.";
                return;
            }

            var file = _file;
            var pal = file.Palettes[_paletteOverride];
            var linkers = file.Palettes.Where(p => ReferenceEquals(p.LinksTo, pal)).ToList();
            int users = file.Sprites.Count(s => ReferenceEquals(s.Palette, pal));
            Palette? fallback = pal.LinksTo ?? file.Palettes.FirstOrDefault(p => !ReferenceEquals(p, pal));

            bool HasSpriteFallback()
            {
                if (users == 0 || fallback is not null && file.Palettes.Contains(fallback)) return true;
                ShowPopup("Delete Palette", $"Can't delete palette {PaletteLabel(pal)}: sprites use it and no valid fallback palette is available.");
                string target = fallback is null ? "none" : $"{fallback.Group},{fallback.Index} is not in the palette table";
                CrashLog.Error("OnDeletePalette", $"refused: sprites use {pal.Group},{pal.Index}, and fallback target is {target}");
                return false;
            }

            if (!HasSpriteFallback()) return;
            if (HasPendingColorAdjust() && _editingPalette is { Source: PaletteSource.Embedded } editingPalette
                && (ReferenceEquals(editingPalette, pal) || linkers.Contains(editingPalette)))
            {
                var pending = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pending == PendingAdjustChoice.Cancel) return;
                if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(pal)) return;
                if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(pal)) return;
            linkers = file.Palettes.Where(p => ReferenceEquals(p.LinksTo, pal)).ToList();
            users = file.Sprites.Count(s => ReferenceEquals(s.Palette, pal));
            fallback = pal.LinksTo ?? file.Palettes.FirstOrDefault(p => !ReferenceEquals(p, pal));
            if (!HasSpriteFallback()) return;

            if (linkers.Count > 0 || users > 0)
            {
                string fallbackName = fallback is { Source: PaletteSource.External }
                    ? PaletteDisplayName(file, fallback)
                    : $"palette {PaletteKey(file, fallback)}";
                var impact = new List<string>();
                if (linkers.Count == 1)
                    impact.Add("1 palette links to this palette. It will be unlinked with its current colors preserved.");
                else if (linkers.Count > 1)
                    impact.Add($"{linkers.Count} palettes link to this palette. They will be unlinked with their current colors preserved.");
                if (users == 1)
                    impact.Add($"1 sprite uses this palette and will be reassigned to {fallbackName}.");
                else if (users > 1)
                    impact.Add($"{users} sprites use this palette and will be reassigned to {fallbackName}.");
                if (users > 0) impact.Add("Their displayed colors may change.");
                if (!await Dialogs.Confirm(this, "Delete Palette?", string.Join(" ", impact), "Delete"))
                    return;
            }

            if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(pal)) return;
            int at = file.Palettes.IndexOf(pal);
            bool editingPaletteRemoved = ReferenceEquals(_editingPalette, pal);
            var editingView = _editingView;
            int editingSwatch = _selectedSwatch;
            bool editorWasEnabled = ColorEditor.IsEnabled;
            var affected = file.Sprites.Where(s => ReferenceEquals(s.Palette, pal)).ToList();
            var linkStates = linkers.Select(p => PaletteLinkState.Capture(p)).ToList();

            void ClearRemovedPaletteEditor()
            {
                if (!editingPaletteRemoved) return;
                _editingPalette = null;
                _editingView = null;
                _selectedSwatch = -1;
                ColorEditor.IsEnabled = false;
                ASlider.IsEnabled = false;
                ResetColorAdjustBase();
            }

            void RestoreRemovedPaletteEditor()
            {
                if (!editingPaletteRemoved || editingView is null
                    || editingSwatch < 0 || editingSwatch >= pal.Colors.Length) return;
                _editingPalette = pal;
                _editingView = editingView;
                _selectedSwatch = editingSwatch;
                var color = pal.Colors[editingSwatch];
                string label = $"Index {editingSwatch}: RGB {color.R}, {color.G}, {color.B}, A {color.A}";
                SwatchInfo.Text = label;
                _suppressEvents = true;
                RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                _suppressEvents = false;
                ColorEditor.IsEnabled = editorWasEnabled;
                ASlider.IsEnabled = editorWasEnabled && CanEditPaletteAlpha(pal);
                SpriteCanvas.PaintColorIndex = editingSwatch;
                ResetColorAdjustBase();
            }

            Edit("delete palette",
                () =>
                {
                    for (int i = 0; i < linkers.Count; i++)
                        new PaletteLinkState(null, linkStates[i].Colors).Restore(linkers[i]);
                    foreach (var s in affected) s.Palette = fallback;
                    file.Palettes.Remove(pal);
                    _paletteOverride = -1;
                    ClearRemovedPaletteEditor();
                },
                () =>
                {
                    file.Palettes.Insert(Math.Min(at, file.Palettes.Count), pal);
                    for (int i = 0; i < linkers.Count; i++)
                        linkStates[i].Restore(linkers[i]);
                    foreach (var s in affected) s.Palette = pal;
                    _paletteOverride = at;
                    RestoreRemovedPaletteEditor();
                });
            CrashLog.Trace($"[palettes] deleted {pal.Group},{pal.Index}; unlinked {linkers.Count} palette(s) with copied colors");
        }
        catch (Exception ex)
        {
            await Failed("OnDeletePalette", ex);
        }
    }

    private async void OnFindDuplicatePalettes(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file)
            {
                StatusText.Text = "Open a sprite file first.";
                CrashLog.Error("OnFindDuplicatePalettes", "no sprite file is open");
                return;
            }
            if (_pickingFor is not null) CancelPick(restoreSelection: true);

            if (HasPendingColorAdjust() && _editingPalette?.Source == PaletteSource.Embedded)
            {
                var choice = await Dialogs.AskAboutPendingColorAdjust(this);
                if (choice == PendingAdjustChoice.Cancel) return;
                if (!ReferenceEquals(_file, file)) return;
                if (choice == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            var embedded = file.Palettes.Where(p => p.Source == PaletteSource.Embedded).ToList();
            var sets = file.FindDuplicatePalettes(embedded);
            int duplicateCount = sets.Sum(set => set.Count - 1);
            if (duplicateCount == 0)
            {
                StatusText.Text = "No duplicate embedded palettes found.";
                CrashLog.Trace("[palettes] duplicate check found none");
                return;
            }

            var lines = new List<(string Text, Action? Jump)>
            {
                ("Duplicate embedded palettes found:", null),
                ("", null),
            };
            foreach (var set in sets)
            {
                foreach (var duplicate in set.Skip(1))
                    lines.Add(($"{PaletteLabel(duplicate)} matches {PaletteLabel(set[0])}",
                        () => JumpToPalette(duplicate)));
            }
            lines.Add(("", null));
            lines.Add(("The mask color is ignored if its alpha is 0.", null));
            lines.Add(("External palettes are ignored.", null));

            var reportChoice = new TaskCompletionSource<DuplicatePaletteAction?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var report = Dialogs.ShowReport(this, "Duplicate Palettes", lines, new (string Label, string Tip, Action Click)[]
            {
                ("Merge", "Make all sprites use the first palette. Delete the later ones.",
                    () => reportChoice.TrySetResult(DuplicatePaletteAction.Merge)),
                ("Link", "The later palettes become linked to the first.",
                    () => reportChoice.TrySetResult(DuplicatePaletteAction.Link)),
            });
            report.Closed += (_, _) => reportChoice.TrySetResult(null);
            var action = await reportChoice.Task;
            if (action is null || !ReferenceEquals(_file, file)) return;

            // The report stays open while the file is edited, so check the duplicates again before acting.
            var currentSets = file.FindDuplicatePalettes(file.Palettes.Where(p => p.Source == PaletteSource.Embedded));
            if (currentSets.Count != sets.Count || currentSets.Zip(sets)
                .Any(pair => !pair.First.SequenceEqual(pair.Second)))
            {
                if (await Dialogs.AskFindDuplicatesAgain(this)) OnFindDuplicatePalettes(null, new RoutedEventArgs());
                return;
            }

            bool merge = action == DuplicatePaletteAction.Merge;
            int linkCount = sets.Sum(set => set.Skip(1).Count(p => !ReferenceEquals(p.LinksTo, set[0])));
            if (!merge && linkCount == 0)
            {
                StatusText.Text = "Duplicate embedded palettes are already linked.";
                return;
            }

            Palette? selected = _paletteOverride >= 0 && _paletteOverride < file.Palettes.Count
                ? file.Palettes[_paletteOverride]
                : null;
            Palette? selectedAfter = selected;
            if (merge && selected is not null)
                selectedAfter = sets.FirstOrDefault(set => set.Skip(1).Any(p => ReferenceEquals(p, selected)))?[0] ?? selected;
            bool editingPaletteAffected = _editingPalette is { } editing
                && sets.Any(set => set.Any(p => ReferenceEquals(p, editing)));

            var before = file.SnapshotPixelLinks();
            PixelLinkSnapshot? after = null;
            int changed = 0;
            Edit(merge ? "merge duplicate palettes" : "link duplicate palettes",
                () =>
                {
                    if (after is null)
                    {
                        changed = file.ShareDuplicatePalettes(sets, discard: merge);
                        after = file.SnapshotPixelLinks();
                    }
                    else after.Restore();

                    _paletteOverride = selectedAfter is null ? -1 : file.Palettes.IndexOf(selectedAfter);
                    if (editingPaletteAffected)
                    {
                        _editingPalette = null;
                        _editingView = null;
                        _selectedSwatch = -1;
                        ColorEditor.IsEnabled = false;
                        SyncAdjustCommitButton();
                    }
                },
                () =>
                {
                    before.Restore();
                    _paletteOverride = selected is null ? -1 : file.Palettes.IndexOf(selected);
                    if (editingPaletteAffected)
                    {
                        _editingPalette = null;
                        _editingView = null;
                        _selectedSwatch = -1;
                        ColorEditor.IsEnabled = false;
                        SyncAdjustCommitButton();
                    }
                });

            string verb = merge ? "Merged" : "Linked";
            string paletteNoun = changed == 1 ? "palette" : "palettes";
            string groupNoun = sets.Count == 1 ? "group" : "groups";
            CrashLog.Trace($"[palettes] {verb.ToLowerInvariant()} {changed} duplicate embedded palettes in {sets.Count} groups");
            StatusText.Text = $"{verb} {changed} duplicate embedded {paletteNoun} in {sets.Count} {groupNoun}.";
        }
        catch (Exception ex)
        {
            await Failed("OnFindDuplicatePalettes", ex);
        }
    }

    private async void OnFindUnusedPalettes(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is not { } file)
            {
                StatusText.Text = "Open a sprite file first.";
                CrashLog.Error("OnFindUnusedPalettes", "no sprite file is open");
                return;
            }

            var unused = file.Palettes
                .Where(p => p.Source == PaletteSource.Embedded && !p.IsCharacterPalette
                    && !file.Sprites.Any(s => ReferenceEquals(s.Palette, p)))
                .ToList();
            var lines = new List<(string Text, Action? Jump)>
            {
                (unused.Count == 0 ? "No unused palettes found." : $"Unused palettes: {unused.Count}", null),
                ("Ignored group 1 character palettes and all external palettes.", null),
            };
            if (unused.Count > 0)
            {
                lines.Add(("", null));
                foreach (var palette in unused)
                    lines.Add(($"Palette {PaletteKey(file, palette)}", () => JumpToPalette(palette)));
            }

            Dialogs.ShowReport(this, "Unused Palettes", lines);
            CrashLog.Trace($"[palettes] found {unused.Count} unused embedded palette(s); ignored group 1 and external palettes");
        }
        catch (Exception ex)
        {
            await Failed("OnFindUnusedPalettes", ex);
        }
    }

    private static Rgba[] BlankColors(int count)
    {
        var c = new Rgba[Math.Max(count, 2)];
        c[0] = new Rgba(0, 0, 0, 0); // the mask
        for (int i = 1; i < c.Length; i++) c[i] = new Rgba(0, 0, 0, 255);
        return c;
    }

    /// <summary>Sorts embedded SFF palettes by their displayed group/index, then external palettes by import order. Palette objects stay intact, so sprites and the active selection keep their references.</summary>
    private void OnSortPalettesAscending(object? sender, RoutedEventArgs e)
    {
        if (_file is null || _file.Palettes.Count < 2) return;
        var file = _file;
        var before = file.Palettes.ToList();
        var externalOrdinals = ExternalPaletteOrdinals(file);
        int GroupOrder(Palette p) => p.Source == PaletteSource.External ? int.MaxValue : p.Group;
        int IndexOrder(Palette p) => p.Source == PaletteSource.External ? externalOrdinals[p] : p.Index;
        var after = before.OrderBy(p => p.Source == PaletteSource.External ? IsTempPalette(p) ? 2 : 1 : 0)
            .ThenBy(GroupOrder).ThenBy(IndexOrder).ToList();
        if (before.SequenceEqual(after)) return;

        var selected = _paletteOverride >= 0 && _paletteOverride < before.Count
            ? before[_paletteOverride]
            : null;

        void Apply(List<Palette> order)
        {
            file.Palettes = [.. order];
            _paletteOverride = selected is null ? -1 : file.Palettes.IndexOf(selected);
            RebuildPaletteList();
            RefreshPaletteTab();
            UpdateSffInfo();
        }

        _undo.Do("sort palettes", () => Apply(after), () => Apply(before));
        MarkDirty();
        StatusText.Text = "Palettes sorted by source, group and number.";
    }

    private void SelectPalette(Palette pal)
    {
        if (_file is null) return;
        _paletteOverride = _file.Palettes.IndexOf(pal);
        RebuildPaletteList(); // already reselects PaletteList by identity, using _paletteOverride above
        if (PaletteList is { SelectedIndex: < 0 } && PaletteFilterBox is { Text.Length: > 0 })
        {
            // Not in the current filter's results — clear it, same as the sprite
            // list does when jumping to a sprite the filter is hiding.
            _suppressEvents = true;
            PaletteFilterBox.Text = "";
            _suppressEvents = false;
            RebuildPaletteList();
        }

        _suppressEvents = true;
        PaletteBox.SelectedIndex = _paletteOverride + 1;
        _suppressEvents = false;
        RefreshPaletteTab();
        if (ActivePalette() is { } selectedPalette)
        {
            _editingPalette = selectedPalette;
            _editingView = BigSwatches;
            _selectedSwatch = selectedPalette.Colors.Length > 0 ? 0 : -1;
            ColorEditor.IsEnabled = true;
            ASlider.IsEnabled = CanEditPaletteAlpha(selectedPalette);
            if (_selectedSwatch >= 0)
            {
                var color = selectedPalette.Colors[_selectedSwatch];
                _suppressEvents = true;
                RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                _suppressEvents = false;
            }
            ResetColorAdjustBase();
        }
    }

    private static Dictionary<Palette, Sprite> V1PaletteCarriers(SffFile file)
    {
        var carriers = new Dictionary<Palette, Sprite>();
        if (!SffVersions.IsV1(file.Version)) return carriers;

        Palette? previous = null;
        foreach (var sprite in file.Sprites)
        {
            if (sprite.SharesWith is not null || sprite.Palette is not { } palette) continue;
            if (!ReferenceEquals(palette, previous) && palette.Source == PaletteSource.Embedded)
                carriers.TryAdd(palette, sprite);
            previous = palette;
        }
        return carriers;
    }

    private static Dictionary<Palette, int> ExternalPaletteOrdinals(SffFile file) => ExternalPaletteOrdinals(file.Palettes);

    /// <summary>Numbers file palettes (group F) and temp palettes (group T) separately, in list order.</summary>
    private static Dictionary<Palette, int> ExternalPaletteOrdinals(IEnumerable<Palette> palettes)
    {
        var ordinals = new Dictionary<Palette, int>();
        int files = 0, temps = 0;
        foreach (var palette in palettes)
            if (palette.Source == PaletteSource.External)
                ordinals[palette] = IsTempPalette(palette) ? ++temps : ++files;
        return ordinals;
    }

    /// <summary>SFFv1 stores no palette keys: its own palettes are numbered S1, S2… in list order.
    /// Empty for SFFv2.</summary>
    private static Dictionary<Palette, int> V1PaletteOrdinals(SffFile file)
    {
        var ordinals = new Dictionary<Palette, int>();
        if (!SffVersions.IsV1(file.Version)) return ordinals;
        int n = 0;
        foreach (var palette in file.Palettes)
            if (palette.Source == PaletteSource.Embedded) ordinals[palette] = ++n;
        return ordinals;
    }

    private const string V1GroupToolTip = "S: stored in a sprite; SFFv1 has no palette keys";

    /// <summary>The displayed group of an external palette: T while it has no file, else F.</summary>
    private static string ExternalGroup(Palette p) => IsTempPalette(p) ? "T" : "F";

    private static string PaletteDisplayName(SffFile file, Palette palette,
        IReadOnlyDictionary<Palette, int>? externalOrdinals = null)
    {
        if (palette.Source == PaletteSource.External)
        {
            externalOrdinals ??= ExternalPaletteOrdinals(file);
            return externalOrdinals.TryGetValue(palette, out int ordinal)
                ? $"{ExternalGroup(palette)}{ordinal} {palette.Describe()}"
                : palette.Describe();
        }
        return $"{PaletteKey(file, palette)} palette ({palette.Colors.Length} colors)";
    }

    private static int SpriteOrdinal(SffFile file, Sprite sprite, IReadOnlyDictionary<Sprite, int>? ordinals = null) =>
        ordinals is not null && ordinals.TryGetValue(sprite, out int ordinal)
            ? ordinal
            : file.Sprites.IndexOf(sprite) + 1;

    private PaletteRow ToRow(Palette p, int number, bool v1,
        IReadOnlyDictionary<Palette, int> v1Ordinals,
        IReadOnlyDictionary<Palette, Sprite> carriers,
        IReadOnlyDictionary<Palette, int> externalOrdinals,
        IReadOnlyDictionary<Sprite, int> spriteOrdinals) => new()
    {
        Palette = p,
        Number = number.ToString(CultureInfo.InvariantCulture),
        // External rows use F/T and import order; SFFv1's own use S and list order,
        // as SFFv1 stores no keys; SFFv2 rows use their group/number.
        GroupText = p.Source == PaletteSource.External ? ExternalGroup(p)
            : v1Ordinals.ContainsKey(p) ? "S" : p.Group.ToString(CultureInfo.InvariantCulture),
        IndexText = p.Source == PaletteSource.External ? externalOrdinals[p].ToString(CultureInfo.InvariantCulture)
            : v1Ordinals.TryGetValue(p, out int sn) ? sn.ToString(CultureInfo.InvariantCulture)
            : p.Index.ToString(CultureInfo.InvariantCulture),
        GroupToolTip = p.Source == PaletteSource.External ? ExternalGroupToolTip
            : v1 ? V1GroupToolTip : "Double-click to change this palette's group",
        IndexToolTip = p.Source == PaletteSource.External ? ExternalIndexToolTip
            : v1 ? carriers.TryGetValue(p, out var stored) ? $"Order within its group; stored in sprite {stored.Group},{stored.Index}" : ExternalIndexToolTip
            : "Double-click to change this palette's index",
        SourceText = p.Source == PaletteSource.External
            ? IsTempPalette(p) ? "temp" : "file"
            : v1 && carriers.ContainsKey(p) ? "sprite" : "sff",
        ColorCountText = p.Colors.Length.ToString(CultureInfo.InvariantCulture),
        // Same convention as the sprites' Shared column: the target's # in this list.
        LinkText = p.LinksTo is { } t && _file?.Palettes.IndexOf(t) is int ti and >= 0 ? $"{ti + 1}" : "",
        LinkToolTip = p.Source == PaletteSource.External ? "Only embedded palettes can be linked or merged."
            : v1 ? "Palette links and merges are available in SFFv2." : "Double-click to link, merge, change or unlink this palette.",
        Kind = p.Source == PaletteSource.External
            ? IsTempPalette(p) ? TempPaletteKind : $"Imported from {System.IO.Path.GetFileName(p.SourcePath)}"
            : v1 && carriers.TryGetValue(p, out var carrier)
                ? $"Palette carried by sprite #{SpriteOrdinal(_file!, carrier, spriteOrdinals)} in SFFv1"
                : v1 ? "SFFv1 palette not carried by a sprite" : "Embedded in the sprite file",
    };

    /// <summary>Matches on group/index (same "5000" / "5000,10" convention as the sprite filter) or the source text ("sff" or file format).</summary>
    private static bool MatchesPalette(PaletteRow row, string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Length > 1
            && (parts[0][0] is 'S' or 's' or 'F' or 'f' or 'T' or 't'))
        {
            string prefix = parts[0][..1];
            string ordinal = parts[0][1..];
            if (row.GroupText.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                && row.IndexText.StartsWith(ordinal, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (parts.Length >= 2)
            return row.GroupText.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                && row.IndexText.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase);
        return row.GroupText.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
            || $"{row.GroupText},{row.IndexText}".StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
            || row.SourceText.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildPaletteList()
    {
        if (_file is null)
        {
            var ordinals = ExternalPaletteOrdinals(_standalonePalettes);
            var all = _standalonePalettes.Select((p, i) => new PaletteRow
            {
                Palette = p,
                Number = (i + 1).ToString(CultureInfo.InvariantCulture),
                GroupText = ExternalGroup(p),
                IndexText = ordinals[p].ToString(CultureInfo.InvariantCulture),
                GroupToolTip = ExternalGroupToolTip,
                IndexToolTip = ExternalIndexToolTip,
                SourceText = IsTempPalette(p) ? "temp" : Path.GetExtension(p.SourcePath ?? "").TrimStart('.').ToUpperInvariant(),
                ColorCountText = p.Colors.Length.ToString(CultureInfo.InvariantCulture),
                LinkText = "",
                LinkToolTip = "",
                Kind = Path.GetFileName(p.SourcePath) ?? TempPaletteKind,
            }).ToList();
            var widths = TextTableWidths(PaletteTableHeader, all.Select(r => new[]
                { r.NumberText, r.GroupText, r.IndexText, r.SourceText, r.ColorCountText, r.LinkText }).ToList());
            foreach (var row in all) row.Widths = widths;
            string filter = (PaletteFilterBox?.Text ?? "").Trim();
            var visible = all.Where(r => filter.Length == 0 || MatchesPalette(r, filter)
                || r.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.SourceText.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                || r.Number.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            PaletteBox.ItemsSource = new[] { "Palette file" };
            PaletteBox.SelectedIndex = 0;
            bool standaloneWasSuppressed = _suppressEvents;
            _suppressEvents = true;
            PaletteList.ItemsSource = visible;
            var selected = _standalonePaletteOverride >= 0 && _standalonePaletteOverride < _standalonePalettes.Count
                ? _standalonePalettes[_standalonePaletteOverride] : null;
            if (selected is not null)
            {
                int index = visible.FindIndex(r => ReferenceEquals(r.Palette, selected));
                if (index >= 0) PaletteList.SelectedIndex = index;
            }
            _suppressEvents = standaloneWasSuppressed;
            return;
        }
        bool v1 = SffVersions.IsV1(_file.Version);
        var carriers = V1PaletteCarriers(_file);
        var externalOrdinals = ExternalPaletteOrdinals(_file);
        var spriteOrdinals = _file.Sprites.Select((sprite, i) => (sprite, ordinal: i + 1)).ToDictionary(x => x.sprite, x => x.ordinal);
        var labels = _file.Palettes.Select((p, i) => $"{i + 1}: {PaletteDisplayName(_file, p, externalOrdinals)}").ToList();

        // Preserves whatever suppression the caller already had, rather than
        // hardcoding it back to false — a caller that's suppressed (e.g.
        // HighlightPaletteInList) needs it to stay that way past this call, or
        // its own reselect right after would fire OnPaletteListSelected for
        // real (same class of bug ApplyFilter had).
        bool wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        int keepCombo = PaletteBox.SelectedIndex;
        PaletteBox.ItemsSource = new[] { "Sprite's own" }.Concat(labels).ToList();
        if (keepCombo >= 0 && keepCombo <= labels.Count) PaletteBox.SelectedIndex = keepCombo;

        if (PaletteList is not null)
        {
            var filter = (PaletteFilterBox?.Text ?? "").Trim();
            // Numbers stay absolute (position in _file.Palettes, matching PaletteBox's own
            // labels) even under a filter, so a palette keeps its number when others around
            // it are hidden — same spirit as the action list keeping Action.Number stable.
            var current = _paletteOverride >= 0 && _paletteOverride < _file.Palettes.Count ? _file.Palettes[_paletteOverride] : null;

            var v1Ordinals = V1PaletteOrdinals(_file);
            var all = _file.Palettes.Select((p, i) => ToRow(p, i + 1, v1, v1Ordinals, carriers, externalOrdinals, spriteOrdinals)).ToList();
            // Widths over every palette, so filtering doesn't move the columns.
            var widths = TextTableWidths(PaletteTableHeader, all.Select(r => new[]
                { r.NumberText, r.GroupText, r.IndexText, r.SourceText, r.ColorCountText, r.LinkText }).ToList());
            foreach (var r in all) r.Widths = widths;
            var visible = all
                .Select((r, i) => (Row: r, Index: i))
                .Where(x => filter.Length == 0 || MatchesPalette(x.Row, filter))
                .Select(x => x.Index)
                .ToList();

            // Every sprite edit rebuilds this list, and almost none of them change a palette
            // row. Copy the cells into the rows already on screen instead of replacing
            // ItemsSource, which drops the list's scroll offset and its selection.
            if (PaletteList.ItemsSource is List<PaletteRow> existing && SameVisiblePalettes(existing, visible))
            {
                for (int i = 0; i < visible.Count; i++) existing[i].CopyFrom(all[visible[i]]);
            }
            else
            {
                PaletteList.ItemsSource = visible.Select(i => all[i]).ToList();
                if (current is not null)
                {
                    int idx = visible.FindIndex(i => ReferenceEquals(_file.Palettes[i], current));
                    if (idx >= 0) PaletteList.SelectedIndex = idx;
                }
            }
        }
        _suppressEvents = wasSuppressed;
    }

    /// <summary>True when the rows already handed to the list are these palettes, in order:
    /// nothing was added, removed, reordered or filtered since they were built.</summary>
    private bool SameVisiblePalettes(List<PaletteRow> rows, List<int> visible)
    {
        if (_file is null || rows.Count != visible.Count) return false;
        for (int i = 0; i < visible.Count; i++)
            if (!ReferenceEquals(rows[i].Palette, _file.Palettes[visible[i]])) return false;
        return true;
    }

    /// <summary>
    /// Double-clicking a palette's Grp or Idx cell prompts for the new value,
    /// same convention as OnSpriteCellDoubleTapped. Group/Index are plain
    /// fields with no uniqueness enforced elsewhere (sprites reference a
    /// Palette object directly, not by group/index), but two palettes
    /// sharing a key is still worth flagging — SFFv2 writes it as the
    /// palette's identity, and .act import matches on it — so a collision
    /// asks for confirmation instead of silently allowing it.
    /// </summary>
    private async void OnPaletteCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_file is null || sender is not Control c || c.DataContext is not PaletteRow row) return;
        if (row.Palette.Source == PaletteSource.External || SffVersions.IsV1(_file.Version)) return;
        bool isGroup = (string?)c.Tag == "Group";
        var pal = row.Palette;

        try
        {
            var result = await Dialogs.PromptText(this, isGroup ? "Change group" : "Change index",
                isGroup ? "New group:" : "New index:",
                (isGroup ? pal.Group : pal.Index).ToString(CultureInfo.InvariantCulture));
            if (result is null) return;
            if (!int.TryParse(result.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
            {
                await Dialogs.ShowMessage(this, "Not a Number", "Type a whole number.");
                return;
            }
            if (val < 0 || val > SpriteFieldMax)
            {
                StatusText.Text = _settings.DebugMode
                    ? "Group and Index must be whole numbers of 0 or more."
                    : "Group and Index must be whole numbers from 0 to 65535.";
                return;
            }

            int newGroup = isGroup ? val : pal.Group;
            int newIndex = isGroup ? pal.Index : val;
            if (newGroup != pal.Group || newIndex != pal.Index)
            {
                bool collides = _file.Palettes.Any(p => !ReferenceEquals(p, pal) && p.Group == newGroup && p.Index == newIndex);
                if (collides && !await Dialogs.Confirm(this, "Group/Index Already Used",
                        $"Another palette already uses group {newGroup}, index {newIndex}. Continue anyway?", "Change Anyway"))
                    return;
            }

            CommitPaletteField(pal, newGroup, newIndex);
        }
        catch (Exception ex)
        {
            // async void: an exception here has nowhere else to go — log it
            // explicitly rather than relying on it reaching AppDomain intact.
            CrashLog.Write("OnPaletteCellDoubleTapped", ex);
            await Dialogs.ShowMessage(this, "Something Went Wrong", ex.Message);
        }
    }

    /// <summary>Double-clicking a palette's Linked cell lets an embedded SFFv2 palette
    /// link to or merge into a chosen target, or unlink.</summary>
    private async void OnPaletteLinkCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (_file is not { } file || sender is not Control c || c.DataContext is not PaletteRow row) return;
            var palette = row.Palette;
            if (palette.Source != PaletteSource.Embedded)
            {
                CrashLog.Error("OnPaletteLinkCellDoubleTapped", $"refused external palette {palette.Describe()}");
                await Dialogs.ShowMessage(this, "Cannot Link or Merge Palette", "Only embedded SFFv2 palettes can be linked or merged.");
                return;
            }
            if (SffVersions.IsV1(file.Version))
            {
                CrashLog.Error("OnPaletteLinkCellDoubleTapped", "refused SFFv1 palette link or merge: SFFv1 has no palette link table");
                await Dialogs.ShowMessage(this, "Cannot Link or Merge Palette", "Palette links and merges are available only in SFFv2.");
                return;
            }

            if (HasPendingColorAdjust() && _editingPalette?.Source == PaletteSource.Embedded)
            {
                var pending = await Dialogs.AskAboutPendingColorAdjust(this);
                if (pending == PendingAdjustChoice.Cancel || !ReferenceEquals(_file, file)) return;
                if (pending == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            var choice = await Dialogs.PromptPaletteSharing(this);
            if (!ReferenceEquals(_file, file) || choice is null) return;

            switch (choice.Value)
            {
                case PaletteShareDialogAction.Link:
                case PaletteShareDialogAction.Merge:
                {
                    bool merge = choice.Value == PaletteShareDialogAction.Merge;
                    var currentTarget = palette.LinksTo;
                    var targetChoice = await Dialogs.PromptPaletteLinkTarget(this,
                        currentTarget?.Group.ToString(CultureInfo.InvariantCulture) ?? "",
                        currentTarget?.Index.ToString(CultureInfo.InvariantCulture) ?? "",
                        SpriteFieldMax,
                        (group, index) => ValidatePaletteShareTarget(file, palette, group, index));
                    if (!ReferenceEquals(_file, file) || targetChoice is null) return;
                    if (targetChoice.Action == PaletteTargetDialogAction.PickFromList)
                    {
                        BeginPaletteLinkPick(palette, merge);
                        break;
                    }
                    var target = file.Palettes.Single(p => p.Source == PaletteSource.Embedded
                        && p.Group == targetChoice.Group && p.Index == targetChoice.Index);
                    if (merge) await MergePaletteIntoTarget(file, palette, target);
                    else await ChangePaletteLink(file, palette, target);
                    break;
                }
                case PaletteShareDialogAction.Unlink:
                    if (palette.LinksTo is not null) await ChangePaletteLink(file, palette, null);
                    else
                    {
                        CrashLog.Error("OnPaletteLinkCellDoubleTapped", $"refused unlink of unlinked palette {palette.Describe()}");
                        StatusText.Text = $"Palette {PaletteLabel(palette)} is not linked.";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            await Failed("OnPaletteLinkCellDoubleTapped", ex);
        }
    }

    private string? ValidatePaletteShareTarget(SffFile file, Palette source, int group, int index)
    {
        var matching = file.Palettes.Where(p => p.Group == group && p.Index == index).ToArray();
        var embedded = matching.Where(p => p.Source == PaletteSource.Embedded).ToArray();
        if (embedded.Length == 0)
        {
            if (matching.Any(p => p.Source == PaletteSource.External))
            {
                CrashLog.Error("Palette sharing", $"refused external target at {group},{index}");
                return "Only embedded SFFv2 palettes can be linked or merged.";
            }
            return "No embedded palette has that Group and Index.";
        }
        if (embedded.Length > 1) return "More than one embedded palette has that Group and Index. Pick the intended palette from the list.";
        var error = PaletteShareTargetError(file, source, embedded[0]);
        if (error is not null)
            CrashLog.Error("Palette sharing", $"refused {PaletteDisplayName(file, source)} to {PaletteDisplayName(file, embedded[0])}: {error}");
        return error;
    }

    private static string? PaletteShareTargetError(SffFile file, Palette source, Palette target)
    {
        if (ReferenceEquals(source, target)) return "A palette cannot be its own target.";
        if (target.Source != PaletteSource.Embedded) return "Only embedded SFFv2 palettes can be linked or merged.";
        int sourcePosition = file.Palettes.IndexOf(source);
        int targetPosition = file.Palettes.IndexOf(target);
        if (sourcePosition < 0 || targetPosition < 0) return "The source and target must be in the palette list.";
        if (targetPosition > sourcePosition)
            return $"Palette {target.Group},{target.Index} appears after {source.Group},{source.Index} in the palette list. Choose an earlier palette.";
        if (target.LinksTo is not null) return "That palette is already linked. Choose one with its own colors.";
        return null;
    }

    private void BeginPaletteLinkPick(Palette source, bool merge)
    {
        if (_file is null || !_file.Palettes.Contains(source)) return;
        if (_palettePickingFor is not null) CancelPaletteLinkPick(restoreSelection: true);
        HighlightPaletteInList(source);
        _palettePickingFor = (source, merge);
        StatusText.Text = merge
            ? "Choose a palette to merge into, or press Escape."
            : "Choose a palette to link to, or press Escape.";
    }

    private void CancelPaletteLinkPick(bool restoreSelection)
    {
        if (_palettePickingFor is not { } pick) return;
        _palettePickingFor = null;
        if (restoreSelection) HighlightPaletteInList(pick.Source);
    }

    private async Task CompletePaletteLinkPick(Palette source, Palette target, bool merge)
    {
        if (_file is not { } file || !_file.Palettes.Contains(source))
        {
            CancelPaletteLinkPick(restoreSelection: false);
            return;
        }

        CancelPaletteLinkPick(restoreSelection: true);
        if (merge) await MergePaletteIntoTarget(file, source, target);
        else await ChangePaletteLink(file, source, target);
    }

    private async Task MergePaletteIntoTarget(SffFile file, Palette source, Palette target)
    {
        if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(source)) return;
        if (!file.Palettes.Contains(target))
        {
            CrashLog.Error("Palette merging", "refused target that is no longer in the palette table");
            await Dialogs.ShowMessage(this, "Cannot Merge Palettes", "The target palette is no longer in the palette table.");
            return;
        }
        if (PaletteShareTargetError(file, source, target) is { } error)
        {
            CrashLog.Error("Palette merging", $"refused {PaletteDisplayName(file, source)} into {PaletteDisplayName(file, target)}: {error}");
            await Dialogs.ShowMessage(this, "Cannot Merge Palettes", error);
            return;
        }

        int sourceNumber = file.Palettes.IndexOf(source) + 1;
        int targetNumber = file.Palettes.IndexOf(target) + 1;
        string sourceName = PaletteDisplayName(file, source);
        string targetName = PaletteDisplayName(file, target);
        int spriteUsers = file.Sprites.Count(s => ReferenceEquals(s.Palette, source));
        int linkedPalettes = file.Palettes.Count(p => !ReferenceEquals(p, source) && ReferenceEquals(p.LinksTo, source));
        string impact = $"Palette #{sourceNumber} ({source.Group},{source.Index}) will be removed. "
            + $"{spriteUsers} sprite(s) and {linkedPalettes} linked palette(s) will be redirected to "
            + $"palette #{targetNumber} ({target.Group},{target.Index}) and use its colors. Their displayed colors may change.";
        if (!await Dialogs.Confirm(this, "Merge Palettes", impact, "Merge")) return;
        if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(source) || !file.Palettes.Contains(target)) return;

        Palette? selected = _paletteOverride >= 0 && _paletteOverride < file.Palettes.Count
            ? file.Palettes[_paletteOverride]
            : null;
        Palette? selectedAfter = ReferenceEquals(selected, source) ? target : selected;
        bool editingPaletteRemoved = ReferenceEquals(_editingPalette, source);
        var before = file.SnapshotPixelLinks();
        PixelLinkSnapshot? after = null;
        (int Sprites, int Palettes) changed = default;

        void ClearRemovedPaletteEditor()
        {
            if (!editingPaletteRemoved) return;
            _editingPalette = null;
            _editingView = null;
            _selectedSwatch = -1;
            ColorEditor.IsEnabled = false;
            SyncAdjustCommitButton();
        }

        void ApplyMerged()
        {
            if (after is null)
            {
                changed = file.MergePalette(source, target);
                after = file.SnapshotPixelLinks();
            }
            else after.Restore();
            _paletteOverride = selectedAfter is null ? -1 : file.Palettes.IndexOf(selectedAfter);
            ClearRemovedPaletteEditor();
        }

        void RestoreBefore()
        {
            before.Restore();
            _paletteOverride = selected is null ? -1 : file.Palettes.IndexOf(selected);
            ClearRemovedPaletteEditor();
        }

        Edit("merge palette", ApplyMerged, RestoreBefore);
        CrashLog.Trace($"[palettes] merged {sourceName} into {targetName}; redirected {changed.Sprites} sprite(s) and {changed.Palettes} linked palette(s)");
        StatusText.Text = $"Merged palette {PaletteLabel(source)} into {PaletteLabel(target)}; redirected {changed.Sprites} sprite(s) and {changed.Palettes} linked palette(s).";
    }

    private async Task ChangePaletteLink(SffFile file, Palette source, Palette? target)
    {
        if (!ReferenceEquals(_file, file) || !file.Palettes.Contains(source)) return;
        if (target is not null)
        {
            if (!file.Palettes.Contains(target))
            {
                CrashLog.Error("Palette linking", "refused target that is no longer in the palette table");
                return;
            }
            if (target.Source != PaletteSource.Embedded)
            {
                CrashLog.Error("Palette linking", $"refused external target {target.Describe()}");
                await Dialogs.ShowMessage(this, "Cannot Link Palette", "Only embedded SFFv2 palettes can be linked.");
                return;
            }
            if (file.Palettes.IndexOf(target) > file.Palettes.IndexOf(source))
            {
                CrashLog.Error("Palette linking", $"refused {PaletteDisplayName(file, source)} to later palette {PaletteDisplayName(file, target)}");
                await Dialogs.ShowMessage(this, "Cannot Link to Later Palette",
                    $"Palette {PaletteDisplayName(file, target)} appears after {PaletteDisplayName(file, source)} in the palette list. Choose an earlier palette.");
                return;
            }
            if (target.LinksTo is not null)
            {
                CrashLog.Error("Palette linking", $"refused linked target {PaletteDisplayName(file, target)}");
                await Dialogs.ShowMessage(this, "Cannot Link to a Linked Palette", "Choose an embedded palette that is not already linked.");
                return;
            }
            if (PaletteShareTargetError(file, source, target) is { } error)
            {
                CrashLog.Error("Palette linking", $"refused {PaletteDisplayName(file, source)} to {PaletteDisplayName(file, target)}: {error}");
                await Dialogs.ShowMessage(this, "Cannot Link Palette", error);
                return;
            }
            if (ReferenceEquals(source.LinksTo, target))
            {
                StatusText.Text = $"Palette {PaletteDisplayName(file, source)} already links to {PaletteDisplayName(file, target)}.";
                return;
            }

            int sourceNumber = file.Palettes.IndexOf(source) + 1;
            int targetNumber = file.Palettes.IndexOf(target) + 1;
            if (!await Dialogs.Confirm(this, "Link Palette",
                    $"Palette #{sourceNumber} ({source.Group},{source.Index}) will use the colors from palette #{targetNumber} ({target.Group},{target.Index}). Sprites and palettes using it will also show those colors.", "Link"))
                return;
            if (!ReferenceEquals(_file, file)) return;
        }
        else if (source.LinksTo is null)
        {
            return;
        }

        var before = PaletteLinkState.Capture(source);
        var after = target is null
            ? new PaletteLinkState(null, before.Colors)
            : new PaletteLinkState(target, target.Colors.ToArray());

        void Apply(PaletteLinkState state)
        {
            state.Restore(source);
            if (ReferenceEquals(_editingPalette, source))
            {
                _editingPalette = null;
                _editingView = null;
                _selectedSwatch = -1;
                ColorEditor.IsEnabled = false;
                SyncAdjustCommitButton();
            }
        }

        Edit(target is null ? "unlink palette" : "link palette",
            () => Apply(after), () => Apply(before));
        CrashLog.Trace(target is null
            ? $"[palettes] unlinked {PaletteDisplayName(file, source)} with its current colors"
            : $"[palettes] linked {PaletteDisplayName(file, source)} to {PaletteDisplayName(file, target)}");
        StatusText.Text = target is null
            ? $"Unlinked palette {PaletteDisplayName(file, source)}; its colors are unchanged."
            : $"Linked palette {PaletteDisplayName(file, source)} to {PaletteDisplayName(file, target)}.";
    }

    private sealed record PaletteLinkState(Palette? Target, Rgba[] Colors)
    {
        public static PaletteLinkState Capture(Palette palette) => new(palette.LinksTo, palette.Colors.ToArray());

        public void Restore(Palette palette)
        {
            palette.LinksTo = null;
            palette.Colors = Colors.ToArray();
            palette.LinksTo = Target;
        }
    }

    /// <summary>Commits a palette's Group/Index through the normal undo path. Doesn't touch
    /// Colors or the .act Dirty flag — those track whether an exported .act would differ,
    /// which group/index (not stored in .act) has no bearing on.</summary>
    private void CommitPaletteField(Palette pal, int group, int index)
    {
        if (_file is null) return;
        var before = (pal.Group, pal.Index);
        var after = (group, index);
        if (before == after) return;

        void Apply((int Group, int Index) v)
        {
            pal.Group = v.Group;
            pal.Index = v.Index;
            RebuildPaletteList();
        }
        Edit("palette group/index change", () => Apply(after), () => Apply(before), $"palette-fields:{_file.Palettes.IndexOf(pal)}");
    }

    /// <summary>
    /// Highlights a palette's row in PaletteList — cosmetic only. Unlike
    /// SelectPalette, this must never touch _paletteOverride: that also drives
    /// ActivePalette(), which the Sprites tab's canvas and its own Swatches
    /// view read too, so changing it here (e.g. from clicking a color on the
    /// Palette tab) would silently change what the Sprites tab shows —
    /// exactly the cross-tab leak this exists to avoid.
    /// </summary>
    private void HighlightPaletteInList(Palette pal)
    {
        if (_file is null || PaletteList is null) return;
        bool wasSuppressed = _suppressEvents;
        _suppressEvents = true;
        ReselectPaletteRow(pal);
        if (PaletteList.SelectedIndex < 0 && PaletteFilterBox is { Text.Length: > 0 })
        {
            // Not in the current filter's results — clear it, same as
            // SelectPalette does, just without touching _paletteOverride.
            PaletteFilterBox.Text = "";
            RebuildPaletteList();
            ReselectPaletteRow(pal);
        }
        _suppressEvents = wasSuppressed;
    }

    private void OnPaletteFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        RebuildPaletteList();
    }

    // ---- palettes tab -----------------------------------------------------

    private async void OnPaletteListSelected(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_suppressEvents) return;
            if (_file is null)
            {
                DiscardPendingColorAdjust();
                _standalonePaletteOverride = PaletteList.SelectedItem is PaletteRow standaloneRow
                    ? _standalonePalettes.IndexOf(standaloneRow.Palette) : -1;
                _editingPalette = ActivePalette();
                _editingView = _editingPalette is null ? null : BigSwatches;
                _selectedSwatch = _editingPalette is { Colors.Length: > 0 } ? 0 : -1;
                ColorEditor.IsEnabled = _editingPalette is not null;
                ASlider.IsEnabled = _editingPalette is not null && CanEditPaletteAlpha(_editingPalette);
                BigSwatches.Show(_editingPalette);
                if (_editingPalette is { } standalonePalette && _selectedSwatch >= 0)
                {
                    var color = standalonePalette.Colors[_selectedSwatch];
                    _suppressEvents = true;
                    RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                    _suppressEvents = false;
                }
                ResetColorAdjustBase();
                RefreshPaletteTab();
                UpdateTitle();
                return;
            }
            if (_palettePickingFor is { } pick)
            {
                if (PaletteList.SelectedItem is PaletteRow targetRow)
                    await CompletePaletteLinkPick(pick.Source, targetRow.Palette, pick.Merge);
                return;
            }
            // Leaving this palette: any slider preview that wasn't committed is
            // lost, same as if you'd pressed Reset. No dialog — Commit is the
            // one and only way to keep it, so there's nothing left to ask.
            DiscardPendingColorAdjust();

            _paletteOverride = PaletteList.SelectedItem is PaletteRow row ? _file.Palettes.IndexOf(row.Palette) : -1;
            _editingPalette = ActivePalette();
            _editingView = _editingPalette is null ? null : BigSwatches;
            _selectedSwatch = _editingPalette is { Colors.Length: > 0 } ? 0 : -1;
            ColorEditor.IsEnabled = _editingPalette is not null;
            ASlider.IsEnabled = _editingPalette is not null && CanEditPaletteAlpha(_editingPalette);
            RefreshPaletteTab();
            if (_editingPalette is { } selectedPalette && _selectedSwatch >= 0)
            {
                var color = selectedPalette.Colors[_selectedSwatch];
                _suppressEvents = true;
                RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                _suppressEvents = false;
            }
            ResetColorAdjustBase();
        }
        catch (Exception ex)
        {
            await Failed("OnPaletteListSelected", ex);
        }
    }

    /// <summary>Selects a palette's row in the current (possibly filtered) PaletteList without touching the filter — for reverting a canceled selection change, not for jumping to a palette the filter may be hiding (see SelectPalette for that).</summary>
    private void ReselectPaletteRow(Palette pal)
    {
        if (PaletteList?.ItemsSource is not IEnumerable<PaletteRow> rows) return;
        int idx = rows.ToList().FindIndex(r => ReferenceEquals(r.Palette, pal));
        if (idx >= 0) PaletteList.SelectedIndex = idx;
    }

    /// <summary>
    /// The palette tab shows the chosen table large, with the current sprite
    /// beside it so color edits can be judged against real pixels.
    /// </summary>
    private void RefreshPaletteTab()
    {
        if (BigSwatches is null || PalettePreview is null) return;
        var pal = ActivePalette();
        BigSwatches.Show(pal);
        SyncPalettePreviewNav();
        // The preview is a full sprite decode; skip it while the Palettes tab
        // (index 2) is hidden — OnTabChanged refreshes it when the tab returns.
        if (Tabs is null || Tabs.SelectedIndex == 1) PalettePreview.Show(PalettePreviewSprite(), pal, _showMask);
    }

    /// <summary>
    /// From a palette's right-click menu: previews the first sprite that references
    /// this palette, and also selects it when cross-tab sprite sync is enabled.
    /// </summary>
    private async void PreviewFirstSpriteUsingPalette(Palette pal)
    {
        try
        {
            if (_file is not { } file || !file.Palettes.Contains(pal)) return;
            var sprite = file.Sprites.FirstOrDefault(s => ReferenceEquals(s.Palette, pal));
            if (sprite is null)
            {
                StatusText.Text = $"No sprite uses palette {PaletteLabel(pal)}.";
                return;
            }

            if (_settings.SyncSpriteAcrossTabs && !ReferenceEquals(_current, sprite) && HasPendingColorAdjust())
            {
                var choice = await Dialogs.AskAboutPendingColorAdjust(this);
                if (choice == PendingAdjustChoice.Cancel || !ReferenceEquals(_file, file)
                    || !file.Palettes.Contains(pal) || !file.Sprites.Contains(sprite)) return;
                if (choice == PendingAdjustChoice.Commit) CommitPendingColorAdjust();
                else DiscardPendingColorAdjust();
            }

            SelectPalette(pal);
            int position = file.PositionOf(sprite);
            // The target may be hidden by the sprite filter; clearing it brings
            // it back into both the preview and the synchronized sprite selection.
            if (_visible.IndexOf(position) < 0 && FilterBox is { Text.Length: > 0 })
            {
                _suppressEvents = true;
                FilterBox.Text = "";
                _suppressEvents = false;
                ApplyFilter();
            }

            int row = _visible.IndexOf(position);
            if (row < 0) return;
            if (_settings.SyncSpriteAcrossTabs)
            {
                if (!await ActivateSpriteRow(row)) return;
                SelectOnlySpriteRow(row, suppressEvents: true);
                ScrollSpriteIntoView(sprite);
            }

            _palettePreviewSprite = sprite;
            SyncPalettePreviewNav();
            RefreshPaletteTab();
            CrashLog.Trace($"[palettes] preview set to first sprite using {pal.Group},{pal.Index}: #{sprite.Group},{sprite.Index}; sync={_settings.SyncSpriteAcrossTabs}");
            StatusText.Text = $"Previewing sprite {sprite.Group},{sprite.Index}, the first to use palette {PaletteLabel(pal)}.";
        }
        catch (Exception ex)
        {
            await Failed("PreviewFirstSpriteUsingPalette", ex);
        }
    }

    private static readonly FilePickerFileType ActType =
        new("ACT palette files") { Patterns = ["*.act"] };

    private static readonly FilePickerFileType PaletteFilesType =
        new("Palette files (RGBA, ACT, Paint Shop Pro JASC-PAL, indexed PNG, PCX)")
        { Patterns = ["*.rgba", "*.act", "*.pal", "*.png", "*.pcx"] };

    /// <summary>
    /// Palette files stay external until a sprite uses one.
    /// </summary>
    private async void OnImportPaletteFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await PickOpen(new FilePickerOpenOptions
            {
                Title = "Open Palette Files",
                AllowMultiple = true,
                FileTypeFilter = [PaletteFilesType],
            });
            if (files.Count == 0) return;
            await ImportPaletteFiles(files.Select(f => (f.Name, f.TryGetLocalPath())).ToList());
        }
        catch (Exception ex)
        {
            await Failed("OnImportPaletteFiles", ex);
        }
    }

    /// <summary>Opens palette files: standalone when no SFF is open, otherwise added to the SFF (undoable). Also used by Load Session.</summary>
    private async Task ImportPaletteFiles(IReadOnlyList<(string Name, string? Path)> files)
    {
        if (_file is null && !await SettlePendingColorAdjust()) return;
        var file = _file;
        var taken = file?.Palettes.Where(p => p.Group == 1).Select(p => p.Index).ToHashSet() ?? [];
        var added = new List<Palette>();
        var errors = new List<string>();
        int selectedExisting = -1;
        foreach (var (name, localPath) in files)
        {
            if (localPath is not { } path)
            {
                CrashLog.Error("OnImportPaletteFiles", $"{name}: selected file has no local path");
                errors.Add($"{name}: selected file has no local path");
                continue;
            }
            try
            {
                if (file is null && _standalonePalettes.Any(p => p.SourcePath is not null && PathsEqual(p.SourcePath, path)))
                {
                    int existing = _standalonePalettes.FindIndex(p => p.SourcePath is not null && PathsEqual(p.SourcePath, path));
                    selectedExisting = existing;
                    continue;
                }
                var pal = PaletteFileImport.Read(path);
                pal.SourcePath = path;
                pal.Source = PaletteSource.External;
                pal.Group = 1;
                if (file is not null)
                {
                    pal.Index = Enumerable.Range(1, int.MaxValue - 1).First(i => !taken.Contains(i));
                    taken.Add(pal.Index);
                }
                else pal.Index = _standalonePalettes.Count + added.Count + 1;
                added.Add(pal);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"OnImportPaletteFiles {path}", ex);
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (added.Count > 0)
        {
            if (file is null)
            {
                _standalonePalettes.AddRange(added);
                _standalonePaletteOverride = _standalonePalettes.Count - 1;
                foreach (var p in added)
                {
                    p.Dirty = false;
                    _undo.MarkSaved(StandalonePaletteKindFor(p));
                }
                PaletteFilterBox.Text = "";
                RebuildPaletteList();
                _editingPalette = added[^1];
                _editingView = BigSwatches;
                _selectedSwatch = _editingPalette.Colors.Length > 0 ? 0 : -1;
                ColorEditor.IsEnabled = true;
                ASlider.IsEnabled = CanEditPaletteAlpha(_editingPalette);
                if (_selectedSwatch >= 0)
                {
                    var color = _editingPalette.Colors[_selectedSwatch];
                    _suppressEvents = true;
                    RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                    _suppressEvents = false;
                }
                ResetColorAdjustBase();
                RefreshPaletteTab();
                UpdateTitle();
            }
            else
            {
                Edit(added.Count == 1 ? "import palette file" : $"import {added.Count} palette files",
                    () => { file.Palettes.AddRange(added); SelectPalette(added[^1]); },
                    () => { foreach (var p in added) file.Palettes.Remove(p); _paletteOverride = -1; });
            }
            StatusText.Text = added.Count == 1 ? $"Opened {Path.GetFileName(added[0].SourcePath)}"
                : $"Opened {added.Count} palette files";
        }
        else if (file is null && selectedExisting >= 0)
        {
            var palette = _standalonePalettes[selectedExisting];
            _standalonePaletteOverride = selectedExisting;
            PaletteFilterBox.Text = "";
            RebuildPaletteList();
            _suppressEvents = true;
            if (PaletteList.ItemsSource is IEnumerable<PaletteRow> rows)
                PaletteList.SelectedIndex = rows.ToList().FindIndex(row => ReferenceEquals(row.Palette, palette));
            _suppressEvents = false;
            _editingPalette = palette;
            _editingView = BigSwatches;
            _selectedSwatch = palette.Colors.Length > 0 ? 0 : -1;
            ColorEditor.IsEnabled = true;
            ASlider.IsEnabled = CanEditPaletteAlpha(palette);
            if (_selectedSwatch >= 0)
            {
                var color = palette.Colors[_selectedSwatch];
                _suppressEvents = true;
                RSlider.Value = color.R; GSlider.Value = color.G; BSlider.Value = color.B; ASlider.Value = color.A;
                _suppressEvents = false;
            }
            ResetColorAdjustBase();
            RefreshPaletteTab();
            UpdateTitle();
        }
        if (errors.Count > 0)
            await Dialogs.ShowMessage(this, "Some Palette Files Could Not Be Read", string.Join("\n", errors));
    }

    /// <summary>
    /// Saves imported ACT palettes to their source file. Other external files
    /// use Export so their source file is not overwritten.
    /// </summary>
    private async void OnSaveAct(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ActivePalette() is not { } pal)
            {
                StatusText.Text = "Choose a palette first.";
                return;
            }
            if (_file is null)
            {
                await SaveStandalonePalette(pal, askPath: false);
                return;
            }
            if (pal.Source != PaletteSource.External)
            {
                StatusText.Text = "That palette is embedded in the SFF — saving the file saves it.";
                return;
            }
            if (pal.SourcePath is null || !File.Exists(pal.SourcePath))
            {
                OnExportPalette(sender, e);
                return;
            }

            if (await SavePaletteDocument(pal))
            {
                RefreshPaletteTab();
                RefreshSprite();
                StatusText.Text = $"Saved {Path.GetFileName(pal.SourcePath)}.";
            }
        }
        catch (Exception ex)
        {
            await Failed("OnSaveAct", ex);
        }
    }

    private static string? StandalonePaletteLoss(Palette palette, string path, byte[]? sourceBytes)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        var warnings = new List<string>();
        if (extension is ".act" or ".pal" or ".pcx")
        {
            bool alphaLoss = palette.Colors.Length > 0 && (palette.Colors[0].A != 0
                || palette.Colors.Skip(1).Any(c => c.A != 255));
            if (alphaLoss) warnings.Add("Alpha is not stored in this format; index 0 reopens transparent and other entries reopen opaque.");
        }
        if (extension == ".act" && palette.Colors.Length != 256)
            warnings.Add("ACT always stores 256 colors; the palette will be padded or truncated.");
        if (extension == ".pal" && palette.Colors.Length > 256)
            warnings.Add("JASC-PAL stores at most 256 colors.");
        if (extension == ".pcx" && sourceBytes is not null)
        {
            int count = PcxExport.DecodePaletteCount(sourceBytes);
            if (palette.Colors.Length != count) warnings.Add($"This PCX stores {count} palette entries; other entries will be padded or omitted.");
        }
        if (extension == ".png" && sourceBytes is not null)
        {
            int count = PngExport.DecodePaletteCount(sourceBytes);
            if (palette.Colors.Length != count) warnings.Add($"This PNG stores {count} palette entries; other entries will be padded or omitted.");
        }
        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private async Task SaveStandalonePalette(Palette palette, bool askPath)
    {
        if (!askPath && palette.SourcePath is not null && File.Exists(palette.SourcePath))
        {
            await SavePaletteDocument(palette);
            return;
        }

        string extension = Path.GetExtension(palette.SourcePath ?? ".rgba").ToLowerInvariant();
        var (ext, type) = extension switch
        {
            ".act" => ("act", ActType),
            ".pal" => ("pal", JascPalType),
            ".png" => ("png", PalettePngType),
            ".pcx" => ("pcx", PcxType),
            _ => ("rgba", RgbaType),
        };
        // A temp palette has no image pixels, so it can be saved as any pure palette format.
        bool temp = palette.SourcePath is null;
        var file = await PickSave(new FilePickerSaveOptions
        {
            Title = "Save Palette",
            SuggestedFileName = temp ? "palette" : Path.GetFileNameWithoutExtension(palette.SourcePath),
            DefaultExtension = ext,
            FileTypeChoices = temp ? [RgbaType, ActType, JascPalType] : [type],
        });
        if (file?.TryGetLocalPath() is not { } chosenPath) return;
        if (temp && Path.GetExtension(chosenPath).ToLowerInvariant() is (".act" or ".pal" or ".rgba") and var picked)
            ext = picked[1..];
        string path = Path.ChangeExtension(chosenPath, ext);
        byte[]? sourceBytes = Path.GetExtension(path).Equals(Path.GetExtension(palette.SourcePath), StringComparison.OrdinalIgnoreCase)
            ? palette.SourceData : null;
        string? loss = StandalonePaletteLoss(palette, path, sourceBytes);
        if (loss is not null && !await Dialogs.Confirm(this, "Palette Save Loses Information", loss, "Save")) return;
        try
        {
            string targetExtension = Path.GetExtension(path).ToLowerInvariant();
            byte[] output = targetExtension switch
            {
                ".act" => ActFile.Write(palette, palette.SourceActReversed),
                ".pal" => System.Text.Encoding.UTF8.GetBytes(JascPalFile.Write(palette)),
                ".png" when sourceBytes is not null => PngExport.ReplacePalette(sourceBytes, palette),
                ".pcx" when sourceBytes is not null => PcxExport.ReplacePalette(sourceBytes, palette),
                ".png" or ".pcx" => throw new InvalidOperationException("A new PNG or PCX palette document needs source image pixels to preserve."),
                ".rgba" => System.Text.Encoding.UTF8.GetBytes(PaletteRgbaFile.Write(palette)),
                _ => throw new NotSupportedException($"Palette files with extension '{targetExtension}' cannot be saved."),
            };
            Backups.Make(path, _settings.BackupCount);
            File.WriteAllBytes(path, output);
            palette.SourcePath = path;
            if (targetExtension is not (".png" or ".pcx")) palette.SourceData = null;
            else palette.SourceData = output;
            if (targetExtension == ".act") palette.SourceActReversed = true;
            palette.Dirty = false;
            _undo.MarkSaved(StandalonePaletteKindFor(palette));
            RebuildPaletteList();
            UpdateTitle();
            StatusText.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveStandalonePaletteAs", ex);
            await Dialogs.ShowMessage(this, "Could Not Save the Palette", ex.Message);
        }
    }

    private async Task<bool> SavePaletteDocument(Palette palette)
    {
        string? path = palette.SourcePath;
        if (path is null || !File.Exists(path))
        {
            if (!_standalonePalettes.Contains(palette)) return false;
            await SaveStandalonePalette(palette, askPath: true);
            return !palette.Dirty;
        }
        try
        {
            byte[]? sourceBytes = palette.SourceData is null ? null : (byte[])palette.SourceData.Clone();
            string? loss = StandalonePaletteLoss(palette, path, sourceBytes);
            if (loss is not null && !await Dialogs.Confirm(this, "Palette Save Loses Information", loss, "Save")) return false;
            if (!await ConfirmRecoveredSourceWrite(path)) return false;
            if ((Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".pcx", StringComparison.OrdinalIgnoreCase)) && sourceBytes is null)
                throw new FileNotFoundException("The source image is missing.", path);
            Backups.Make(path, _settings.BackupCount);
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".act": ActFile.Save(palette, path, palette.SourceActReversed); break;
                case ".pal": JascPalFile.Save(palette, path); break;
                case ".rgba": PaletteRgbaFile.Save(palette, path); break;
                case ".png":
                    var png = PngExport.ReplacePalette(sourceBytes!, palette);
                    File.WriteAllBytes(path, png);
                    palette.SourceData = png;
                    break;
                case ".pcx":
                    var pcx = PcxExport.ReplacePalette(sourceBytes!, palette);
                    File.WriteAllBytes(path, pcx);
                    palette.SourceData = pcx;
                    break;
                default: throw new NotSupportedException($"Palette files with extension '{Path.GetExtension(path)}' cannot be saved in place.");
            }
            palette.Dirty = false;
            ResolveRecoveredItem("act", path, palette.Group, palette.Index);
            if (_standalonePalettes.Contains(palette)) _undo.MarkSaved(StandalonePaletteKindFor(palette));
            RebuildPaletteList();
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("SavePaletteDocument", ex);
            await Dialogs.ShowMessage(this, "Could Not Save the Palette", ex.Message);
            return false;
        }
    }

    private static readonly FilePickerFileType RgbaType =
        new("RGBA palette files") { Patterns = ["*.rgba"] };

    /// <summary>A palette made in the editor that has no file yet.</summary>
    private static bool IsTempPalette(Palette p) => p.Source == PaletteSource.External && p.SourcePath is null;

    private const string TempPaletteKind = "New palette (not saved yet)";
    private const string ExternalGroupToolTip = "F: from a palette file. T: new, not saved to a file yet.";
    private const string ExternalIndexToolTip = "Order within its group";

    private static readonly FilePickerFileType JascPalType =
        new("JASC-PAL palette files") { Patterns = ["*.pal"] };

    private static readonly FilePickerFileType PalettePngType =
        new("Indexed PNG palette") { Patterns = ["*.png"] };

    /// <summary>Last Export Palette companion format this session; pre-selected next time.</summary>
    private Dialogs.PaletteExportFormat _lastExportFormat = Dialogs.PaletteExportFormat.Act;

    /// <summary>Exports the active palette as .rgba and a chosen ACT, JASC-PAL or indexed PNG companion.</summary>
    private async void OnExportPalette(object? sender, RoutedEventArgs e)
    {
        if (ActivePalette() is not { } pal)
        {
            StatusText.Text = "Choose a palette first.";
            return;
        }

        var format = _lastExportFormat;
        string ext = "act";
        try
        {
            if (await Dialogs.PromptPaletteFormat(this, _lastExportFormat) is not { } choice) return;
            format = _lastExportFormat = choice;
            (ext, var type) = format switch
            {
                Dialogs.PaletteExportFormat.Pal => ("pal", JascPalType),
                Dialogs.PaletteExportFormat.Png => ("png", PalettePngType),
                _ => ("act", ActType),
            };

            var file = await PickSave(new FilePickerSaveOptions
            {
                Title = "Export Palette",
                SuggestedFileName = pal.IsCharacterPalette ? $"pal{pal.Index}" : $"{pal.Group}_{pal.Index}",
                DefaultExtension = ext,
                FileTypeChoices = [type],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            string rgbaPath = Path.ChangeExtension(path, ".rgba");
            if (File.Exists(rgbaPath)
                && !await Dialogs.Confirm(this, "Replace RGBA Palette?",
                    $"{Path.GetFileName(rgbaPath)} already exists and will be replaced.", "Replace"))
                return;

            Backups.Make(rgbaPath, _settings.BackupCount);
            Backups.Make(path, _settings.BackupCount);
            PaletteRgbaFile.Save(pal, rgbaPath);
            switch (format)
            {
                case Dialogs.PaletteExportFormat.Pal:
                    JascPalFile.Save(pal, path);
                    break;
                case Dialogs.PaletteExportFormat.Png:
                    int cell = Settings.Checked(nameof(_settings.PalettePngSwatchSize), _settings.PalettePngSwatchSize,
                        Settings.DefaultPalettePngSwatchSize, v => v is >= 1 and <= 64);
                    PalettePngFile.Save(pal, path, cell);
                    break;
                default:
                    ActFile.Save(pal, path);
                    break;
            }
            StatusText.Text = $"Wrote {Path.GetFileName(rgbaPath)} and {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnExportPalette", ex);
            await Dialogs.ShowMessage(this, "Could Not Export the Palette", ex.Message);
        }
    }
}
