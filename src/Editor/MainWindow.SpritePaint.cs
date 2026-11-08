using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Sff;

namespace Editor;

/// <summary>
/// Paint Mode on the Sprites tab: pencil/eraser/fill/eyedropper on the sprite's
/// own pixel data. Deliberately basic — anything beyond single-pixel tools is
/// out of scope (PNG import already covers more involved editing).
/// Eraser and Select also reach true-color sprites (all four bytes cleared per
/// erased pixel, a zeroed pixel standing in for index 0); Pencil, Fill, Replace
/// and Eyedropper stay indexed-only until a true-color color picker exists.
///
/// Strokes apply live (so painting itself is instant) but stay pending — no main
/// undo entry, only a step in the paint history (MainWindow.PaintHistory.cs) — until Commit, or until switching away asks what to do with
/// them. Commit itself never happens on its own; the dialog's own Commit
/// choice just calls the same thing pressing the button would.
///
/// An optional top layer sits over the sprite's own pixels while a session is
/// pending. The sprite always shows the flattened result, so the tools and the
/// canvas need no layer awareness; Commit keeps the flattened pixels as one edit.
/// Layers are indexed-only.
/// </summary>
public partial class MainWindow
{
    private PaintPixels? _paintBaseline;
    private bool _paintBaselineDirty;
    private Sprite? _paintBaselineSprite;
    private (PaintPixels Pixels, int Width, int Height, int X, int Y, bool[]? Mask)? _pixelClipboard;
    private (Sprite Sprite, PaintPixels Before, int Width, int Height, int SourceWidth, PaintPixels Block)? _floatingPaste;

    /// <summary>The base layer while a top layer exists; null otherwise. Holds the sprite's own pixels, without the top.</summary>
    private byte[]? _paintBaseLayer;
    /// <summary>The top layer, or null when the session has only the base. Index 0 is transparent.</summary>
    private byte[]? _paintTopLayer;
    private bool _paintTopVisible = true;
    private bool _paintBottomVisible = true;
    /// <summary>True when the paint tools draw on the top layer instead of the base.</summary>
    private bool _paintOnTop;

    private void WireSpritePaint()
    {
        SpriteCanvas.PaintStrokeStarted += OnPaintStrokeStarted;
        SpriteCanvas.InverseReplaceStarted += OnInverseReplaceStarted;
        SpriteCanvas.PaintStrokeAllowed = () => _current is null || (!RefuseBorrowerEdit(_current, "paint on") && ActiveLayerCanPaint());
        SpriteCanvas.PaintStrokeFinished += OnPaintStrokeFinished;
        SpriteCanvas.PixelsPainted += RecomposePaintLayers;
        SpriteCanvas.SelectionChanged += OnSelectionChanged;
        SpriteCanvas.SelectionStarted += SettleFloatingPaste;
        SpriteCanvas.SelectionMoved += OnSelectionMoved;
        SpriteCanvas.SelectionMoveFinished += () => PaintStep("move selection");
        SpriteCanvas.ColorPicked += OnPaintColorPicked;
    }

    private async void OnTogglePaintMode(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (PaintModeToggle.IsChecked != true && _paintBaseline is not null)
            {
                if (!PendingPaintChanged())
                {
                    DropPendingPaint(); // nothing to keep
                }
                else
                {
                    PaintModeToggle.IsChecked = true; // stays on until the user decides
                    var choice = await Dialogs.AskAboutPendingPaint(this);
                    if (choice == PendingPaintChoice.Cancel) return;
                    if (choice == PendingPaintChoice.Commit) CommitPendingPaint();
                    else DropPendingPaint();
                    PaintModeToggle.IsChecked = false;
                }
            }
            ApplyPaintMode(PaintModeToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            await Failed("OnTogglePaintMode", ex);
        }
    }

    private void ApplyPaintMode(bool on)
    {
        SpriteCanvas.PaintMode = on;
        SyncPaintToolButtons();
        if (on)
        {
            SelectFirstPaintTool(); // every session starts on the first working tool, Pencil first
        }
        else
        {
            foreach (var b in new[] { PencilToolButton, EraserToolButton, FillToolButton, EyedropperToolButton, ReplaceToolButton, SelectionToolButton, LayersToggle })
                b.IsChecked = false;
        }
        if (!on)
        {
            SettleFloatingPaste();
            SpriteCanvas.SetSelection(null);
        }
        UpdateSelectionControls();
        UpdateToolPanels();
        RefreshLayerPanel();
    }

    /// <summary>
    /// Enables the paint tool buttons for the current sprite, and the brush boxes with them:
    /// Eraser and Select work on indexed and true color; Pencil, Fill, Eyedropper, Replace and
    /// the layers stay behind a palette color. Undecoded sprites have no pixels, so nothing
    /// enables. A kind change can mute the active tool, and the first working tool takes over.
    /// </summary>
    private void SyncPaintToolButtons()
    {
        bool on = SpriteCanvas.PaintMode;
        bool indexed = _current?.Indexed is not null;
        bool hasPixels = _current is not null && PaintPixels.From(_current) is not null;
        PencilToolButton.IsEnabled = on && indexed;
        EraserToolButton.IsEnabled = on && hasPixels;
        FillToolButton.IsEnabled = on && indexed;
        EyedropperToolButton.IsEnabled = on && indexed;
        ReplaceToolButton.IsEnabled = on && indexed;
        SelectionToolButton.IsEnabled = on && hasPixels;
        BrushSizeBox.IsEnabled = on && hasPixels;
        ReplaceBrushSizeBox.IsEnabled = on && indexed;
        if (on && !ToolIsEnabled(SpriteCanvas.ActiveTool)) SelectFirstPaintTool();
        // The materials around the tools refresh with them: layer state follows the sprite.
        SyncPaintLayerControls();
    }

    /// <summary>Whether the given tool's button is enabled, i.e. works for the current sprite.</summary>
    private bool ToolIsEnabled(PaintTool tool) => tool switch
    {
        PaintTool.Pencil => PencilToolButton.IsEnabled,
        PaintTool.Eraser => EraserToolButton.IsEnabled,
        PaintTool.Fill => FillToolButton.IsEnabled,
        PaintTool.Eyedropper => EyedropperToolButton.IsEnabled,
        PaintTool.Replace => ReplaceToolButton.IsEnabled,
        PaintTool.Selection => SelectionToolButton.IsEnabled,
        _ => false,
    };

    /// <summary>Selects the first tool that still works for this sprite, Pencil first, Selection last.</summary>
    private void SelectFirstPaintTool()
    {
        foreach (var button in new[] { PencilToolButton, EraserToolButton, FillToolButton, EyedropperToolButton, ReplaceToolButton, SelectionToolButton })
        {
            if (button.IsEnabled) { OnSelectPaintTool(button, new RoutedEventArgs()); return; }
        }
    }

    /// <summary>Shows only the active tool's option panels, like Replace's colors.</summary>
    private void UpdateToolPanels()
    {
        bool on = SpriteCanvas.PaintMode;
        var tool = SpriteCanvas.ActiveTool;
        BrushPanel.IsVisible = on && tool is PaintTool.Pencil or PaintTool.Eraser;
        SelectionPanel.IsVisible = on && tool == PaintTool.Selection;
        UpdateColorReplacerVisibility();
        ToolOptionsSection.IsVisible = BrushPanel.IsVisible || SelectionPanel.IsVisible || ColorReplacerPanel.IsVisible;
    }

    /// <summary>Replace's panel has its own Brush box; both edit the one shared size.</summary>
    private void OnBrushSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (sender is not NumericUpDown box || BrushSizeBox is null || ReplaceBrushSizeBox is null) return;
        var other = ReferenceEquals(box, BrushSizeBox) ? ReplaceBrushSizeBox : BrushSizeBox;
        if (other.Value != box.Value) other.Value = box.Value;
        SpriteCanvas.BrushSize = (int)(box.Value ?? 1);
    }

    /// <summary>Tool buttons are hand-managed as a radio group — Avalonia's ToggleButton has no GroupName.</summary>
    private void OnSelectPaintTool(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } clicked) return;

        foreach (var b in new[] { PencilToolButton, EraserToolButton, FillToolButton, EyedropperToolButton, ReplaceToolButton, SelectionToolButton })
            b.IsChecked = ReferenceEquals(b, clicked);

        SpriteCanvas.ActiveTool = tag switch
        {
            "Eraser" => PaintTool.Eraser,
            "Fill" => PaintTool.Fill,
            "Eyedropper" => PaintTool.Eyedropper,
            "Replace" => PaintTool.Replace,
            "Selection" => PaintTool.Selection,
            _ => PaintTool.Pencil,
        };
        if (tag != "Selection")
        {
            SettleFloatingPaste();
            SpriteCanvas.SetSelection(null);
        }
        UpdateToolPanels();
    }

    private void OnSelectSelectionShape(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        SpriteCanvas.LassoSelect = tag == "Lasso";
        SpriteCanvas.MagicWandSelect = tag == "MagicWand";
        SpriteCanvas.PolygonSelect = tag == "Polygon";
        RectangleSelectButton.IsChecked = tag == "Rectangle";
        LassoSelectButton.IsChecked = tag == "Lasso";
        MagicWandSelectButton.IsChecked = tag == "MagicWand";
        PolygonSelectButton.IsChecked = tag == "Polygon";
    }

    /// <summary>Right-click on the Sprites tab canvas: Eyedropper is a one-shot pick here, whatever tool is currently active.</summary>
    private void OnSpriteCanvasContextRequested(int boxIndex, Point pos)
    {
        if (boxIndex >= 0)
        {
            ShowContextMenu(SpriteCanvas,
                ("Center Image in Canvas", SpriteCanvas.CenterImage),
                MenuSeparator,
                ("Find Similar Sprites", () => OnFindSimilarSprites(null, new RoutedEventArgs())),
                ("Pin onion skin to this sprite", PinSpriteAsOnion));
            return;
        }
        ShowContextMenu(SpriteCanvas,
            ("Center Image in Canvas", SpriteCanvas.CenterImage),
            MenuSeparator,
            ("Eyedropper: Pick Color Here", () => SpriteCanvas.PickColorAt(pos)),
            MenuSeparator,
            ("Find Similar Sprites", () => OnFindSimilarSprites(null, new RoutedEventArgs())),
            ("Pin onion skin to this sprite", PinSpriteAsOnion));
    }

    /// <summary>Canvas menu: pins the onion skin to the sprite currently shown, and turns it on.</summary>
    private void PinSpriteAsOnion()
    {
        if (_current is null) return;
        _onionGroup = _current.Group;
        _onionIndex = _current.Index;
        if (!_onionSkin) OnToggleOnion(null, new RoutedEventArgs());
        else { SyncOnionControls(); RefreshSprite(); ShowFrame(); }
        StatusText.Text = $"Onion skin pinned to sprite {_onionGroup},{_onionIndex}.";
    }

    /// <summary>
    /// Captures the pixel state a pending session is measured against. Only once
    /// per session: a second stroke on the same sprite must not move the
    /// commit-or-drop baseline forward.
    /// </summary>
    private void CapturePaintBaseline()
    {
        if (_paintBaseline is not null && ReferenceEquals(_paintBaselineSprite, _current)) return;
        if (_current is not { } sprite || PaintPixels.From(sprite) is not { } pixels) return;
        _paintBaseline = pixels.Snapshot();
        _paintBaselineDirty = sprite.PixelsDirty;
        _paintBaselineSprite = sprite;
    }

    /// <summary>
    /// Starts the paint history (and the session's baseline) before a stroke or paste. A sprite
    /// that borrows its pixels never gets here (PaintStrokeAllowed / paste refuse it).
    /// </summary>
    private void OnPaintStrokeStarted()
    {
        SettleFloatingPaste(); // a stroke makes the pasted pixels part of the session
        if (_current is not { } sprite || PaintPixels.From(sprite) is null) return;
        EnsurePaintHistory();
        CapturePaintBaseline();
    }

    private void OnPaintStrokeFinished()
    {
        if (PaintSessionHasChanges()) MarkPaintPendingDirty();
        PaintStep(SpriteCanvas.ActiveTool switch
        {
            PaintTool.Eraser => "eraser stroke",
            PaintTool.Fill => "fill",
            PaintTool.Replace => "replace stroke",
            _ => "pencil stroke",
        });
        SyncPaintButtons();
    }

    private void MarkPaintPendingDirty()
    {
        if (_file is null) return;
        _dirty = true;
        UpdateTitle();
        SyncSaveMenu();
    }

    private void OnSelectionChanged() => UpdateSelectionControls();

    private void OnSelectionMoved()
    {
        if (_floatingPaste is not { } floating || !ReferenceEquals(floating.Sprite, _current)
            || SpriteCanvas.Selection is not { } selection || ActivePaintPixels() is not { } target)
            return;
        var sprite = floating.Sprite;
        // Rebuilt from the pre-paste pixels each time, so a block dragged off the edge comes back intact.
        var pixels = floating.Before.Snapshot();
        int bpp = pixels.TrueColor ? 4 : 1;
        StampPixels(floating.Block.Data, floating.SourceWidth * bpp, floating.Width, floating.Height,
            pixels.Data, bpp, sprite.Width, sprite.Height, selection.X, selection.Y);
        Array.Copy(pixels.Data, target.Data, pixels.Data.Length);
        RecomposePaintLayers();
        sprite.PixelsDirty = true;
        SpriteCanvas.Show(sprite, ActivePalette(), _showMask);
        MarkPaintPendingDirty();
        UpdateSelectionControls();
    }

    private void UpdateSelectionControls()
    {
        if (CopySelectionButton is null || PasteSelectionButton is null) return;
        bool enabled = PaintModeToggle?.IsChecked == true && _current is not null && PaintPixels.From(_current) is not null;
        // A copy keeps its kind (indexed or true color), so Paste needs a matching sprite.
        bool pasteEnabled = enabled && _pixelClipboard is not null
            && (_current?.Direct is not null) == _pixelClipboard!.Value.Pixels.TrueColor;
        CopySelectionButton.IsEnabled = enabled && SpriteCanvas.Selection is { Width: > 0, Height: > 0 };
        PasteSelectionButton.IsEnabled = pasteEnabled;
    }

    private void EraseSelection()
    {
        if (SpriteCanvas.Selection is not { Width: > 0, Height: > 0 } selection) return;
        if (_current is not { Width: int w, Height: int h } sprite) return;
        if (ActivePaintPixels() is not { } target) return;
        if (!ActiveLayerCanPaint()) return;
        if (RefuseBorrowerEdit(sprite, "erase selection")) return;

        int x0 = Math.Max(selection.X, 0);
        int y0 = Math.Max(selection.Y, 0);
        int x1 = Math.Min(selection.X + selection.Width, w);
        int y1 = Math.Min(selection.Y + selection.Height, h);

        bool any = false;
        for (int y = y0; y < y1 && !any; y++)
        {
            for (int x = x0; x < x1 && !any; x++)
            {
                int rx = x - selection.X;
                int ry = y - selection.Y;
                if (selection.Contains(rx, ry) && !target.IsCleared(y * w + x))
                    any = true;
            }
        }
        if (!any) return;

        OnPaintStrokeStarted();
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int rx = x - selection.X;
                int ry = y - selection.Y;
                if (selection.Contains(rx, ry))
                    target.Erase(y * w + x);
            }
        }
        RecomposePaintLayers();
        sprite.PixelsDirty = true;
        SpriteCanvas.Show(sprite, ActivePalette(), _showMask);
        if (PaintSessionHasChanges()) MarkPaintPendingDirty();
        PaintStep("erase selection");
        SyncPaintButtons();
    }

    private void OnCopySelection(object? sender, RoutedEventArgs e)
    {
        if (_current is not { Width: int w, Height: int h } sprite || SpriteCanvas.Selection is not { } selection) return;
        if (PaintPixels.From(sprite) is not { } pixels) return;
        int bpp = pixels.TrueColor ? 4 : 1;
        var copy = new byte[selection.Width * selection.Height * bpp]; // off-canvas and off-lasso parts stay transparent
        for (int y = 0; y < selection.Height; y++)
            for (int x = 0; x < selection.Width; x++)
            {
                int sx = selection.X + x, sy = selection.Y + y;
                if (selection.Contains(x, y) && sx >= 0 && sx < w && sy >= 0 && sy < h)
                    Array.Copy(pixels.Data, (sy * w + sx) * bpp, copy, (y * selection.Width + x) * bpp, bpp);
            }
        _pixelClipboard = (new PaintPixels(copy, pixels.TrueColor), selection.Width, selection.Height, selection.X, selection.Y, selection.Mask);
        UpdateSelectionControls();
        StatusText.Text = $"Copied selection {selection.Width}×{selection.Height}.";
    }

    /// <summary>
    /// Pastes the clipboard back at the spot it was copied from, as a floating
    /// selection, switching to the Select tool so it can be dragged right away.
    /// Clamped to the sprite if it has shrunk since the copy.
    /// </summary>
    private void OnPasteSelection(object? sender, RoutedEventArgs e)
    {
        if (_current is not { Width: int w, Height: int h } sprite || _pixelClipboard is not { } clip) return;
        if (!ActiveLayerCanPaint()) return;
        if (RefuseBorrowerEdit(sprite, "paste into")) return;
        if (ActivePaintPixels() is not { } target) return;
        if (clip.Pixels.TrueColor != target.TrueColor) return; // the Paste button already checks this

        int width = Math.Min(clip.Width, sprite.Width);
        int height = Math.Min(clip.Height, sprite.Height);
        int x = Math.Clamp(clip.X, 0, sprite.Width - width);
        int y = Math.Clamp(clip.Y, 0, sprite.Height - height);

        OnPaintStrokeStarted(); // also settles any earlier floating paste
        var beforePaste = target.Snapshot();
        int bpp = target.TrueColor ? 4 : 1;
        StampPixels(clip.Pixels.Data, clip.Width * bpp, width, height, target.Data, bpp, sprite.Width, sprite.Height, x, y);
        RecomposePaintLayers();

        _floatingPaste = (sprite, beforePaste, width, height, clip.Width, new PaintPixels(clip.Pixels.Data, target.TrueColor));
        OnSelectPaintTool(SelectionToolButton, new RoutedEventArgs());
        SpriteCanvas.SetSelection(new PixelSelection(x, y, clip.Width, clip.Height, clip.Mask)); // clamped and cropped like width/height
        sprite.PixelsDirty = true;
        SpriteCanvas.Show(sprite, ActivePalette(), _showMask);
        MarkPaintPendingDirty();
        PaintStep("paste");
        SyncPaintButtons();
        StatusText.Text = $"Pasted floating selection {width}×{height}. Drag it, then commit.";
    }

    private void OnCommitPaint(object? sender, RoutedEventArgs e) => CommitPendingPaint();

    /// <summary>Discards the pending session and its paint history, like Discard and the Sounds tab's Reset. No undo.</summary>
    private void OnResetPaint(object? sender, RoutedEventArgs e)
    {
        if (_paintBaseline is null) return;
        SpriteCanvas.SetSelection(null);
        DropPendingPaint();
        StatusText.Text = "Reset pending paint changes.";
    }

    /// <summary>Turns the pending session into one real, undoable edit.</summary>
    private void CommitPendingPaint()
    {
        if (_paintBaseline is not { } before || _paintBaselineSprite is not { } sprite) return;
        void SetPixels(Sprite s, byte[] data)
        {
            if (before.TrueColor) s.Direct = data; else s.Indexed = data;
        }

        if (!SessionMatchesSprite(before, sprite))
        {
            // The sprite's pixels were replaced under the session, e.g. a palette mapping
            // turned it indexed; the session can't be applied to them, so it dissolves.
            DissolvePaintSession();
            return;
        }

        bool changed = PaintPixels.From(sprite) is { } after && !before.Same(after);
        if (changed)
        {
            var afterCopy = PaintPixels.From(sprite)!.Snapshot();
            bool afterDirty = sprite.PixelsDirty;
            bool beforeDirty = _paintBaselineDirty;

            // The session already applied itself live, so record the reversal
            // rather than re-applying — same pattern as the axis-drag undo entry.
            _undo.Record("paint sprite",
                () => { SetPixels(sprite, (byte[])before.Data.Clone()); sprite.PixelsDirty = beforeDirty; AfterEdit(); },
                () => { SetPixels(sprite, (byte[])afterCopy.Data.Clone()); sprite.PixelsDirty = afterDirty; AfterEdit(); });
            MarkDirty();
        }

        _paintBaseline = null;
        _paintBaselineSprite = null;
        _floatingPaste = null;
        ClearPaintLayers(); // the sprite already shows the flattened result
        ClearPaintHistory();
        SyncPaintButtons();
        if (!changed) SyncDirtyFromUndo(); // after the session is cleared, so a leftover empty layer can't count as a change
    }

    /// <summary>Reverts the pending session's pixels and drops it — no undo entry, since nothing was ever committed.</summary>
    private void DropPendingPaint()
    {
        if (_paintBaseline is not { } before || _paintBaselineSprite is not { } sprite) return;
        if (!SessionMatchesSprite(before, sprite))
        {
            // The sprite's pixels changed under the session; don't resurrect the old ones.
            DissolvePaintSession();
            if (ReferenceEquals(sprite, _current)) SpriteCanvas.Show(_current, ActivePalette(), _showMask);
            return;
        }
        if (before.TrueColor) sprite.Direct = before.Data; else sprite.Indexed = before.Data;
        sprite.PixelsDirty = _paintBaselineDirty;
        _paintBaseline = null;
        _paintBaselineSprite = null;
        _floatingPaste = null;
        ClearPaintLayers();
        ClearPaintHistory();
        SyncPaintButtons();
        if (ReferenceEquals(sprite, _current)) SpriteCanvas.Show(_current, ActivePalette(), _showMask);
        SyncDirtyFromUndo();
    }

    /// <summary>
    /// True when the sprite's pixel data is still the kind and size the session recorded.
    /// A palette mapping or sprite replace can convert a sprite under a pending session;
    /// applying such a session would resurrect old pixels over the new ones, so commit and
    /// drop dissolve it instead.
    /// </summary>
    private static bool SessionMatchesSprite(PaintPixels baseline, Sprite sprite) =>
        PaintPixels.From(sprite) is { } now
        && now.TrueColor == baseline.TrueColor
        && now.Data.Length == baseline.Data.Length;

    /// <summary>True when the pending session's pixels differ from what they were before it started.</summary>
    private bool PendingPaintChanged() =>
        _paintBaseline is { } before && _paintBaselineSprite is not null
        && PaintPixels.From(_paintBaselineSprite) is { } now && !before.Same(now);

    /// <summary>Ends a pending session without applying or undoing it: its baseline no longer matches the sprite.</summary>
    private void DissolvePaintSession()
    {
        _paintBaseline = null;
        _paintBaselineSprite = null;
        _floatingPaste = null;
        ClearPaintLayers();
        ClearPaintHistory();
        SyncPaintButtons();
        SpriteCanvas.SetSelection(null);
        SyncDirtyFromUndo();
        StatusText.Text = "The sprite's pixels changed while the session was pending; its paint changes were dropped.";
    }

    /// <summary>
    /// Called before switching away from a sprite (or the Sprites tab) with a
    /// pending paint session. True to proceed with the switch, false to stay
    /// put (the caller reverts whatever selection triggered this).
    /// </summary>
    private async Task<bool> ResolvePendingPaintBeforeSwitch()
    {
        if (_paintBaseline is null) return true;
        if (!PendingPaintChanged())
        {
            DropPendingPaint(); // nothing to commit, e.g. only an empty layer was added
            return true;
        }
        var choice = await Dialogs.AskAboutPendingPaint(this);
        switch (choice)
        {
            case PendingPaintChoice.Commit: CommitPendingPaint(); return true;
            case PendingPaintChoice.Discard: DropPendingPaint(); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Eyedropper picked a color — feed it back through the normal swatch-click
    /// path (which also sets SpriteCanvas.PaintColorIndex) so the RGBA editor
    /// and swatch highlight stay in sync with what Paint Mode is using. Also
    /// shown in the status bar, since the RGBA editor itself lives on the
    /// Palettes tab and isn't visible while painting on the Sprites tab.
    /// </summary>
    private void OnPaintColorPicked(int index)
    {
        if (_armedReplaceSlot is int slot) { SetReplaceSlot(slot, index); return; }

        // Highlight it in the sprite panel's grid, same as clicking that swatch directly
        Swatches.SelectOnly(index);
        OnSwatchClicked(ActivePalette(), Swatches, index);
        StatusText.Text = $"Eyedropper picked index {index}.";
    }

    /// <summary>
    /// Writes a pasted block over the target, clipped to the target's edges. A pixel the
    /// source draws transparent (index 0; alpha 0 for true color) leaves the target as it
    /// was, so the block doesn't erase what is under it.
    /// </summary>
    private static void StampPixels(byte[] source, int sourceStride, int width, int height,
        byte[] target, int bpp, int targetWidth, int targetHeight, int x, int y)
    {
        for (int row = 0; row < height; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= targetHeight) continue;
            for (int col = 0; col < width; col++)
            {
                int tx = x + col;
                int o = row * sourceStride + col * bpp;
                bool transparent = bpp == 1 ? source[o] == 0 // index 0 leaves what is under it
                                            : source[o + 3] == 0; // as drawn: alpha 0 leaves what is under it
                if (tx >= 0 && tx < targetWidth && !transparent)
                    Array.Copy(source, o, target, (ty * targetWidth + tx) * bpp, bpp);
            }
        }
    }

    // ---- Layers -------------------------------------------------------------

    /// <summary>The pixels the paint tools write to: the active layer (indexed), or the sprite's own pixels when there is no top layer.</summary>
    private PaintPixels? ActivePaintPixels() =>
        SpriteCanvas.PaintTarget is { } layer ? new PaintPixels(layer, false)
        : _current is { } sprite ? PaintPixels.From(sprite) : null;

    /// <summary>Paint goes only to a visible layer; a hidden one would change pixels nobody can see.</summary>
    private bool ActiveLayerCanPaint()
    {
        if (_paintBaseLayer is null) return true;
        if (_paintOnTop ? _paintTopVisible : _paintBottomVisible) return true;
        StatusText.Text = (_paintOnTop ? "Show the top layer" : "Show the bottom layer") + " to paint on it.";
        return false;
    }

    private bool LayersShown => PaintModeToggle.IsChecked == true && LayersToggle.IsChecked == true;

    private void OnToggleLayers(object? sender, RoutedEventArgs e) => RefreshLayerPanel();

    /// <summary>
    /// Closing the panel (or leaving Paint Mode) merges the visible layers into the
    /// bottom, since the panel is the only way to reach the top.
    /// </summary>
    private void RefreshLayerPanel()
    {
        if (!LayersShown && _paintTopLayer is not null) MergePaintLayers();
        SyncPaintLayerControls();
    }

    /// <summary>Adds an empty top layer to the pending session, starting the session if needed.</summary>
    private void OnAddPaintLayer(object? sender, RoutedEventArgs e)
    {
        if (_current is not { Indexed: { } pixels } sprite || _paintTopLayer is not null) return;
        if (RefuseBorrowerEdit(sprite, "add a layer to")) return;

        EnsurePaintHistory();
        CapturePaintBaseline();
        _paintBaseLayer = (byte[])pixels.Clone();
        _paintTopLayer = new byte[pixels.Length];
        _paintTopVisible = true;
        _paintBottomVisible = true;
        _paintOnTop = true;
        AfterPaintLayersChanged();
        PaintStep("add layer");
        StatusText.Text = "Added a top layer. Commit merges it into the sprite.";
    }

    /// <summary>Discards the top layer. The sprite goes back to the bottom layer's pixels.</summary>
    private void OnDeletePaintLayer(object? sender, RoutedEventArgs e)
    {
        if (_current is not { Indexed: { } pixels } || _paintBaseLayer is not { } baseLayer || _paintTopLayer is null) return;
        EnsurePaintHistory();
        SettleFloatingPaste();
        baseLayer.CopyTo(pixels, 0);
        _paintBaseLayer = null;
        _paintTopLayer = null;
        _paintOnTop = false;
        _paintTopVisible = true;
        _paintBottomVisible = true;
        AfterPaintLayersChanged();
        PaintStep("delete layer");
        StatusText.Text = "Deleted the top layer.";
    }

    /// <summary>
    /// Bakes the visible layers into the bottom layer and drops the top. The sprite
    /// already shows that result, so only the layers go; the session stays pending.
    /// </summary>
    private void OnMergeLayers(object? sender, RoutedEventArgs e)
    {
        if (_paintTopLayer is null) return;
        MergePaintLayers();
        StatusText.Text = "Merged the layers into the bottom layer.";
    }

    /// <summary>The sprite already shows the merged result, so merging only drops the layers and keeps the session pending.</summary>
    private void MergePaintLayers()
    {
        EnsurePaintHistory();
        SettleFloatingPaste();
        ClearPaintLayers();
        PaintStep("merge layers");
        SyncPaintButtons();
    }

    /// <summary>Bottom/Top toggles: which layer the paint tools draw on.</summary>
    private void OnSelectPaintLayer(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        SettleFloatingPaste();
        _paintOnTop = tag == "Top" && _paintTopLayer is not null;
        SpriteCanvas.PaintTarget = ActivePaintLayerPixels();
        SyncPaintLayerControls();
    }

    private void OnBottomLayerVisibleChanged(object? sender, RoutedEventArgs e)
    {
        if (_paintBaseLayer is null || _paintTopLayer is null) return;
        EnsurePaintHistory();
        SettleFloatingPaste();
        _paintBottomVisible = BottomLayerVisibleBox.IsChecked == true;
        AfterPaintLayersChanged();
        PaintStep(_paintBottomVisible ? "show bottom layer" : "hide bottom layer");
    }

    private void OnTopLayerVisibleChanged(object? sender, RoutedEventArgs e)
    {
        if (_paintTopLayer is null) return;
        EnsurePaintHistory();
        SettleFloatingPaste();
        _paintTopVisible = TopLayerVisibleBox.IsChecked == true;
        AfterPaintLayersChanged();
        PaintStep(_paintTopVisible ? "show top layer" : "hide top layer");
    }

    /// <summary>Flattens the layers into the sprite's pixels: the visible bottom, then every visible non-transparent top pixel over it.</summary>
    private void RecomposePaintLayers()
    {
        if (_paintBaseLayer is not { } baseLayer || _paintTopLayer is not { } top
            || _paintBaselineSprite?.Indexed is not { } dest || dest.Length != baseLayer.Length || dest.Length != top.Length)
            return;
        if (_paintBottomVisible) baseLayer.CopyTo(dest, 0);
        else Array.Clear(dest, 0, dest.Length);
        if (!_paintTopVisible) return;
        for (int i = 0; i < dest.Length; i++)
            if (top[i] != Sprite.TransparentIndex) dest[i] = top[i];
    }

    /// <summary>The pixels of the layer the tools draw on, or null when there is no top layer (the sprite's own pixels are used).</summary>
    private byte[]? ActivePaintLayerPixels() => _paintOnTop ? _paintTopLayer : _paintBaseLayer;

    /// <summary>A stroke, paste or drag can't be half-finished across a layer change, so it is settled first.</summary>
    private void SettleFloatingPaste() => _floatingPaste = null;

    /// <summary>Re-flattens, points the canvas at the active layer, and redraws after the layers change.</summary>
    private void AfterPaintLayersChanged()
    {
        RecomposePaintLayers();
        SpriteCanvas.PaintTarget = ActivePaintLayerPixels();
        if (_current is { } sprite) SpriteCanvas.Show(sprite, ActivePalette(), _showMask);
        SyncPaintButtons();
        SyncPaintLayerControls();
    }

    /// <summary>Commit and Reset end the session, so the layers go with it.</summary>
    private void ClearPaintLayers()
    {
        _paintBaseLayer = null;
        _paintTopLayer = null;
        _paintTopVisible = true;
        _paintBottomVisible = true;
        _paintOnTop = false;
        SpriteCanvas.PaintTarget = null;
        SyncPaintLayerControls();
    }

    private void SyncPaintLayerControls()
    {
        if (LayersToggle is null) return;
        bool on = PaintModeToggle.IsChecked == true;
        bool hasTop = _paintTopLayer is not null;
        LayersToggle.IsEnabled = on && _current?.Indexed is not null;
        LayerPanel.IsVisible = LayersShown;
        // Layers are indexed-only; Add Layer comes back with true-color layers.
        AddLayerButton.IsEnabled = on && !hasTop && _current?.Indexed is not null;
        DeleteLayerButton.IsEnabled = MergeLayersButton.IsEnabled = on && hasTop;
        BottomLayerButton.IsEnabled = TopLayerButton.IsEnabled = on && hasTop;
        BottomLayerButton.IsChecked = hasTop && !_paintOnTop; // no top layer: nothing to pick, so nothing shown as picked
        TopLayerButton.IsChecked = hasTop && _paintOnTop;
        BottomLayerVisibleBox.IsEnabled = on && hasTop;
        BottomLayerVisibleBox.IsChecked = _paintBottomVisible;
        TopLayerVisibleBox.IsEnabled = on && hasTop;
        TopLayerVisibleBox.IsChecked = _paintTopVisible;
    }
}
