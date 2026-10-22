using System.Globalization;
using Air;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sff;

namespace Editor;

/// <summary>
/// A rectangle drawn over the sprite, in coordinates relative to its axis. The
/// engine measures collision boxes from the axis rather than the sprite's corner,
/// and does not shift them by an animation element's offset.
/// </summary>
public sealed class BoxOverlay
{
    public int X1, Y1, X2, Y2;
    public Color Color;

    /// <summary>Position within its own kind (Clsn1[i] / Clsn2[i]), for the on-screen label.</summary>
    public int Index;

    /// <summary>Whatever the owner needs to map this back to its own model.</summary>
    public object? Tag;

    public int Left => Math.Min(X1, X2);
    public int Top => Math.Min(Y1, Y2);
    public int Right => Math.Max(X1, X2);
    public int Bottom => Math.Max(Y1, Y2);

    public void Normalize() => (X1, Y1, X2, Y2) = (Left, Top, Right, Bottom);
}

/// <summary>Which part of a box a drag has hold of.</summary>
internal enum BoxGrip
{
    None, Body,
    Left, Right, Top, Bottom,
    TopLeft, TopRight, BottomLeft, BottomRight,
}

/// <summary>The basic pixel tools available in Paint Mode.</summary>
public enum PaintTool { Pencil, Eraser, Fill, Eyedropper, Replace, Selection }

/// <summary>A pixel region in sprite-local coordinates; Mask (Width×Height, row-major) limits it to a lasso shape.</summary>
public readonly record struct PixelSelection(int X, int Y, int Width, int Height, bool[]? Mask = null)
{
    /// <summary>True when the pixel at (x, y), relative to X and Y, is selected.</summary>
    public bool Contains(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && (Mask is null || Mask[y * Width + x]);

    /// <summary>Keeps the top-left width×height part, cropping the mask to match.</summary>
    public PixelSelection Cropped(int width, int height)
    {
        if (width == Width && height == Height) return this;
        bool[]? mask = null;
        if (Mask is not null)
        {
            mask = new bool[width * height];
            for (int y = 0; y < Math.Min(height, Height); y++)
                for (int x = 0; x < Math.Min(width, Width); x++)
                    mask[y * width + x] = Mask[y * Width + x];
        }
        return this with { Width = width, Height = height, Mask = mask };
    }
}

/// <summary>
/// Draws one sprite with its axis at a crosshair, an optional translucent
/// "onion skin" sprite behind it, and any collision boxes over it.
///
/// Pointer interaction is context-sensitive rather than mode-gated: a box (or
/// its handle) under the pointer takes the click and drags or resizes; failing
/// that, the sprite itself takes the click and drags either its axis or, in
/// animation-offset mode, the element's offset; failing that, the drag pans the
/// view. There is no separate "now editing boxes" toggle to turn on first.
/// </summary>
public class SpriteView : Control
{
    /// <summary>Selectable scales, ascending (settings.json ZoomLevels; set once at startup, before any view exists). Below 1 the sprite is shrunk so large ones fit.</summary>
    public static double[] ZoomLevels { get; set; } = Settings.DefaultZoomLevels;

    /// <summary>Index of the step nearest actual size (1), for Actual Size and new views.</summary>
    private static int ZoomOne
    {
        get
        {
            int best = 0;
            for (int i = 1; i < ZoomLevels.Length; i++)
                if (Math.Abs(Math.Log(ZoomLevels[i])) < Math.Abs(Math.Log(ZoomLevels[best]))) best = i;
            return best;
        }
    }

    private const int HandleSize = 8; // screen pixels, fixed regardless of zoom

    /// <summary>Onion-skin overlay opacity (settings.json OnionOpacity; set at startup).</summary>
    public static double OnionOpacity { get; set; } = Settings.DefaultOnionOpacity;

    /// <summary>
    /// Both set from outside (MainWindow.ApplyTheme) so the canvas — and the
    /// checkerboard drawn over it — follows the app's theme, instead of
    /// Background changing per theme while the checkerboard's second color
    /// stayed a fixed dark shade regardless (harsh in the Light theme, which
    /// is what this was actually about).
    /// </summary>
    public IBrush Background { get; set; } = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28));
    public IBrush CheckerDark { get; set; } = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x30));
    private DrawingBrush? _checkerBrush;
    private IBrush? _checkerBrushColor;
    private int _checkerBrushSize;
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xff, 0xff, 0xff)));
    /// <summary>Opacity of the full-length guides relative to the crosshair color.</summary>
    private const double GuideOpacity = 0x60 / 255.0;

    /// <summary>Black base + white dashes on top, so the brush outline reads against any sprite color or theme.</summary>
    private static readonly IPen BrushOutlineBase = new Pen(Brushes.Black, 1);
    private static readonly IPen BrushOutlineDash = new Pen(Brushes.White, 1, new DashStyle([2, 2], 0));

    private Sprite? _sprite;
    private WriteableBitmap? _bitmap;
    /// <summary>_bitmap belongs to _bitmapCache, which disposes it.</summary>
    private bool _bitmapCached;
    private Dictionary<(Sprite, Palette?, bool), WriteableBitmap>? _bitmapCache;

    /// <summary>While on, decoded bitmaps are kept per sprite, palette and mask and reused.
    /// Only for when pixels can't change (playback); turning it off frees them.</summary>
    public bool CacheBitmaps
    {
        get => _bitmapCache is not null;
        set
        {
            if (value) { _bitmapCache ??= new(); return; }
            if (_bitmapCache is null) return;
            foreach (var bmp in _bitmapCache.Values)
                if (!ReferenceEquals(bmp, _bitmap)) bmp.Dispose();
            _bitmapCache = null;
            _bitmapCached = false; // the shown bitmap is ours again
        }
    }
    /// <summary>Color-inverted copy of _bitmap, alpha untouched — the Sub blend's darkening pass multiplies by this instead of the real colors.</summary>
    private WriteableBitmap? _invertedBitmap;
    /// <summary>Solid-black copy of _bitmap, alpha untouched — masks the Add/Sub dest pre-scale to the sprite's own opaque pixels.</summary>
    private WriteableBitmap? _silhouetteBitmap;
    /// <summary>Built on first use: only Add/Sub/SubAdd frames need them.</summary>
    private WriteableBitmap InvertedBitmap => _invertedBitmap ??= MakeBitmap(_sprite!, Invert(_sprite!.ToBgra(_palette, _showMask)));
    private WriteableBitmap SilhouetteBitmap => _silhouetteBitmap ??= MakeBitmap(_sprite!, Blacken(_sprite!.ToBgra(_palette, _showMask)));
    private Palette? _palette;
    private bool _showMask;

    /// <summary>
    /// When set, a left click/drag over the sprite paints instead of dragging
    /// the axis. Panning (a click outside the sprite) and box editing are
    /// unaffected either way.
    /// </summary>
    public bool PaintMode { get; set; }
    public PaintTool ActiveTool { get; set; } = PaintTool.Pencil;

    /// <summary>Palette index the Pencil/Fill tools paint with.</summary>
    public int PaintColorIndex { get; set; }

    /// <summary>
    /// When true, the next left click on the sprite is a one-shot Eyedropper
    /// pick (regardless of PaintMode/ActiveTool) and this clears itself
    /// afterward. Used by the color replacer's "click a square, then click
    /// the sprite" slots, so arming a slot doesn't require Paint Mode to
    /// already be on or the tool to already be Eyedropper.
    /// </summary>
    public bool ArmedForExternalPick { get; set; }

    /// <summary>Side length of the Pencil/Eraser's square brush, in sprite pixels. Fill and Eyedropper ignore it.</summary>
    public int BrushSize { get; set; } = 1;

    /// <summary>
    /// The Replace tool's (from index -> to index) pairs. Painting with this
    /// tool stamps the same square brush as Pencil, but only touches pixels
    /// whose CURRENT index is a key here, replacing it with the paired value —
    /// everything else under the brush is left alone. Null/empty means the
    /// brush touches nothing (nowhere to look anything up).
    /// </summary>
    public IReadOnlyDictionary<byte, byte>? ReplaceMap { get; set; }

    /// <summary>New → Old, for Shift-strokes with Replace. Leaves out a New that several pairs share.</summary>
    public IReadOnlyDictionary<byte, byte>? InverseReplaceMap { get; set; }

    /// <summary>Shift is held over the canvas: the next Replace stroke runs New → Old.</summary>
    private bool _inverseHeld;
    /// <summary>The current Replace stroke runs New → Old (fixed at the press).</summary>
    private bool _inverseStroke;

    /// <summary>Raised when a Shift-stroke with Replace starts.</summary>
    public event Action? InverseReplaceStarted;

    /// <summary>
    /// Pixels the paint tools write to when a top layer is active; null paints the
    /// sprite's own pixels. The eyedropper always reads the sprite's flattened pixels.
    /// </summary>
    public byte[]? PaintTarget { get; set; }

    /// <summary>Raised after a paint tool changes pixels and before the canvas redraws, so the owner can flatten layers first.</summary>
    public event Action? PixelsPainted;

    private bool _painting;
    /// <summary>Set when the stroke in progress is an Eyedropper pick: it moves the color but
    /// changes no pixel, so it must not open a pending paint session or mark the file changed.</summary>
    private bool _pickingStroke;
    private bool _selecting;
    private bool _movingSelection;
    private Point _selectionStart;
    private PixelSelection _selectionMoveOriginal;
    private Point _selectionMoveStart;
    private PixelSelection? _selection;
    /// <summary>The lasso path in progress, in sprite pixel units; null when not drawing a lasso.</summary>
    private List<Point>? _lassoPoints;
    private bool[]? _outlineMask;
    private List<(int X1, int Y1, int X2, int Y2)> _outlineRuns = [];

    /// <summary>Press position for telling a Select click (clears) from a drag.</summary>
    private Point _selectPressPos;
    private bool _selectDragged;
    /// <summary>A press off the sprite that clears the selection on release unless it turns into a pan.</summary>
    private bool _clearSelectionOnClick;

    /// <summary>Pixels the pointer must move before a Select press counts as a drag (settings.json SelectionDragThresholdPixels).</summary>
    public static double SelectDragThreshold { get; set; } = Settings.DefaultSelectionDragThresholdPixels;

    /// <summary>When set, a Select drag draws a freehand lasso instead of a rectangle.</summary>
    public bool LassoSelect { get; set; }
    private Point? _hoverPos;
    private Point? _cursorPosition;

    public event Action? PaintStrokeStarted;

    /// <summary>Asked before a stroke that changes pixels starts; false cancels it (the owner explains why).</summary>
    public Func<bool>? PaintStrokeAllowed { get; set; }
    public event Action? PaintStrokeFinished;
    public event Action? SelectionChanged;
    public event Action? SelectionMoved;
    /// <summary>A selection drag ended (SelectionMoved also fires on every move during it).</summary>
    public event Action? SelectionMoveFinished;
    /// <summary>Raised when a drag starts a new selection or a click clears it (not when an existing one is moved).</summary>
    public event Action? SelectionStarted;

    /// <summary>The current rectangular pixel selection, if any.</summary>
    public PixelSelection? Selection => _selection;

    /// <summary>Sets a pixel selection, clamped to the currently displayed sprite.</summary>
    public void SetSelection(PixelSelection? selection)
    {
        if (selection is null || _sprite is null)
        {
            _selection = null;
        }
        else
        {
            int width = Math.Clamp(selection.Value.Width, 1, _sprite.Width);
            int height = Math.Clamp(selection.Value.Height, 1, _sprite.Height);
            _selection = selection.Value.Cropped(width, height) with
            {
                X = Math.Clamp(selection.Value.X, 0, _sprite.Width - width),
                Y = Math.Clamp(selection.Value.Y, 0, _sprite.Height - height),
            };
        }
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Raised by the Eyedropper tool with the index it picked.</summary>
    public event Action<int>? ColorPicked;

    /// <summary>
    /// Raised instead of <see cref="ColorPicked"/> when the picked pixel has no palette index to
    /// report, i.e. a true-color sprite. Carries the color itself, which is the only thing a
    /// picker can be given there.
    /// </summary>
    public event Action<Rgba>? ColorPickedValue;

    private Sprite? _onionSprite;
    private WriteableBitmap? _onionBitmap;
    private Point _onionOffset;
    private Palette? _onionPalette;
    private bool _onionGrayscale;

    private int _zoomIdx = ZoomOne;
    private Point _pan;
    private Point _dragFrom;

    private bool _panning;
    private bool _draggingSprite;
    private BoxGrip _grip = BoxGrip.None;
    private int _boxPressIndex = -1;
    private bool _boxDragMoved;

    public event Action? AxisChanged;
    public event Action? AxisDragStarted;
    public event Action? AxisDragFinished;
    public event Action<int, int>? CursorPositionChanged;
    public event Action? CursorPositionCleared;

    /// <summary>
    /// When set, dragging the sprite body raises the offset events below
    /// instead of moving the sprite's own axis. Used by the Animations canvas,
    /// where a drag on the sprite should move the frame's offset, not the
    /// sprite's axis — those are different things edited from different tabs.
    /// </summary>
    public bool DragChangesOffset { get; set; }
    private bool _offsetDraggingEnabled = true;
    public bool OffsetDraggingEnabled
    {
        get => _offsetDraggingEnabled;
        set
        {
            _offsetDraggingEnabled = value;
            if (!value && DragChangesOffset) _draggingSprite = false;
        }
    }
    public event Action? OffsetDragStarted;
    public event Action<int, int>? OffsetDelta; // dx, dy in sprite pixels since the last event
    public event Action? OffsetDragFinished;

    /// <summary>
    /// When true, this view can never write to the Sprite it's showing —
    /// not its axis, not its pixels, nothing. Every left-button gesture
    /// (drag, paint, box edit) collapses to plain panning; a left-drag never
    /// starts axis/offset dragging, painting, box editing, or a selection.
    /// Used by the Palettes tab's preview, which shows a Sprite by reference
    /// (possibly the exact object the Sprites tab has open) purely to check
    /// a palette against it — it has no business editing sprite data, and
    /// used to be able to drag the axis by accident (silently, with no undo
    /// entry). ApplyTool also checks this directly, as a second guard on the
    /// one method that actually writes pixels, independent of how it gets
    /// called.
    /// </summary>
    public bool ReadOnly { get; set; }

    public event Action? BoxSelected;
    public event Action? BoxChanged;
    public event Action? BoxEditStarted;
    public event Action? BoxEditFinished;
    public event Action<int, Point>? BoxContextRequested;

    public bool Checkerboard { get; set; } = true;
    /// <summary>Transparency checkerboard square size, in screen pixels (settings.json CheckerSize).</summary>
    public int CheckerSize { get; set; } = Settings.DefaultCheckerSize;
    public bool ShowBounds { get; set; }
    public bool FullCrosshair { get; set; }
    /// <summary>False hides the axis cross and the guides (palette preview).</summary>
    public bool ShowCrosshair { get; set; } = true;
    public CrosshairStyle Crosshair { get; set; } = CrosshairStyle.Default;
    /// <summary>Null = no background image.</summary>
    public CanvasBackground? BackgroundImage { get; set; }
    public bool ShowOnionSkin { get; set; }

    /// <summary>This canvas's own onion opacity (Animations tab); null = the shared OnionOpacity.</summary>
    public double? OnionOpacityOverride { get; set; }

    private IList<BoxOverlay> _ghostBoxes = new List<BoxOverlay>();

    /// <summary>Onion-skinned Clsn: another frame's boxes, drawn dashed; never clickable.</summary>
    public IList<BoxOverlay> GhostBoxes
    {
        get => _ghostBoxes;
        set { _ghostBoxes = value ?? new List<BoxOverlay>(); InvalidateVisual(); }
    }

    private IList<BoxOverlay> _boxes = new List<BoxOverlay>();
    private readonly HashSet<int> _selectedBoxes = [];
    private readonly HashSet<int> _editingBoxes = [];

    public IList<BoxOverlay> Boxes
    {
        get => _boxes;
        set
        {
            _boxes = value ?? new List<BoxOverlay>();
            _selectedBoxes.Clear();
            _editingBoxes.Clear();
            SelectedBox = -1;
            BoxSelected?.Invoke();
            InvalidateVisual();
        }
    }

    public int SelectedBox { get; private set; } = -1;
    public IReadOnlyCollection<int> SelectedBoxes => _selectedBoxes;
    public IReadOnlyCollection<int> EditingBoxes => _editingBoxes;

    public Point ElementOffset { get; set; }
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public double ElementScaleX { get; set; } = 1;
    public double ElementScaleY { get; set; } = 1;

    /// <summary>Degrees, counter-clockwise (confirmed against engine convention).</summary>
    public double ElementAngle { get; set; }

    /// <summary>
    /// Rough approximation only, not engine-accurate. See DrawBlended() for
    /// what's exact and what's a stand-in.
    /// </summary>
    public Blend ElementBlend { get; set; } = Blend.None;

    public double Zoom => _sprite is null ? 1 : ZoomLevels[_zoomIdx];

    /// <summary>Raised whenever the zoom level changes, so a status line can follow.</summary>
    public event Action? ZoomChanged;

    public SpriteView()
    {
        Focusable = true;
        ClipToBounds = true;
        // Covers Avalonia's own first open as well as RestartTipTimer's.
        ToolTip.AddToolTipOpeningHandler(this, (_, e) => { if (!TipAllowedHere()) e.Cancel = true; });
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    /// <summary>Shows a sprite, rebuilding the bitmap from the given palette.</summary>
    public void Show(Sprite? sprite, Palette? palette, bool showMask, bool preserveZoom = false)
    {
        bool changedSprite = !ReferenceEquals(_sprite, sprite);
        _sprite = sprite;
        if (sprite is null && !preserveZoom && _zoomIdx != ZoomOne)
        {
            _zoomIdx = ZoomOne;
            _pan = default;
        }
        if (changedSprite)
        {
            _selection = null;
            SelectionChanged?.Invoke();
        }
        _palette = palette;
        _showMask = showMask;
        RefreshBitmap();
        InvalidateVisual();
    }

    /// <summary>
    /// Redecodes the bitmap from the current sprite/palette, e.g. after painting.
    /// Drops the inverted and silhouette copies; DrawBlended rebuilds them on demand
    /// with the same alpha channel as _bitmap, so they line up pixel-for-pixel with it.
    /// </summary>
    private void RefreshBitmap()
    {
        if (!_bitmapCached) _bitmap?.Dispose();
        _invertedBitmap?.Dispose();
        _silhouetteBitmap?.Dispose();
        _bitmap = _invertedBitmap = _silhouetteBitmap = null;
        _bitmapCached = false;
        if (_sprite is not { Width: > 0, Height: > 0 } || !_sprite.HasPixels) return;

        if (_bitmapCache is not null)
        {
            var key = (_sprite, _palette, _showMask);
            if (!_bitmapCache.TryGetValue(key, out _bitmap))
                _bitmapCache[key] = _bitmap = MakeBitmap(_sprite, _sprite.ToBgra(_palette, _showMask));
            _bitmapCached = true;
            return;
        }
        _bitmap = MakeBitmap(_sprite, _sprite.ToBgra(_palette, _showMask));
    }

    /// <summary>Flips RGB to their complements in place, keeping alpha as-is.</summary>
    private static byte[] Invert(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(255 - bgra[i]);
            bgra[i + 1] = (byte)(255 - bgra[i + 1]);
            bgra[i + 2] = (byte)(255 - bgra[i + 2]);
        }
        return bgra;
    }

    /// <summary>Zeroes RGB in place, keeping alpha as-is — a black silhouette of the sprite.</summary>
    private static byte[] Blacken(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
        return bgra;
    }

    /// <summary>
    /// Shows a translucent reference sprite behind the main one, to help line
    /// up consecutive sprites or animation frames. Pass null to clear it.
    /// Grayscale strips color so the onion skin reads as a shape/outline
    /// reference rather than competing with the current sprite's own colors.
    /// </summary>
    /// <param name="reuse">Keep the current bitmap when sprite, palette and grayscale match; only safe while pixels can't change (playback).</param>
    public void ShowOnion(Sprite? sprite, Palette? palette, Point offset = default, bool grayscale = false, bool reuse = false)
    {
        bool same = reuse && _onionBitmap is not null && ReferenceEquals(_onionSprite, sprite)
            && ReferenceEquals(_onionPalette, palette) && _onionGrayscale == grayscale;
        _onionSprite = sprite;
        _onionOffset = offset;
        if (same) { InvalidateVisual(); return; }
        _onionPalette = palette;
        _onionGrayscale = grayscale;
        _onionBitmap?.Dispose();
        _onionBitmap = sprite is { Width: > 0, Height: > 0 } && sprite.HasPixels
            ? MakeBitmap(sprite, grayscale ? Desaturate(sprite.ToBgra(palette, false)) : sprite.ToBgra(palette, false))
            : null;
        InvalidateVisual();
    }

    /// <summary>Converts a BGRA buffer to grayscale in place using standard luma weights, keeping alpha untouched.</summary>
    private static byte[] Desaturate(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            byte lum = (byte)Math.Clamp((int)Math.Round(0.114 * b + 0.587 * g + 0.299 * r), 0, 255);
            bgra[i] = lum; bgra[i + 1] = lum; bgra[i + 2] = lum;
        }
        return bgra;
    }

    private static WriteableBitmap MakeBitmap(Sprite sprite, byte[] pixels)
    {
        var bmp = new WriteableBitmap(new PixelSize(sprite.Width, sprite.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        return bmp;
    }

    public void StepZoom(int delta)
    {
        int next = Math.Clamp(_zoomIdx + delta, 0, ZoomLevels.Length - 1);
        if (next == _zoomIdx) return;
        _zoomIdx = next;
        RefreshCursorPosition();
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Zoom step and pan, so a project tab can keep its own view.</summary>
    public (int ZoomIndex, Point Pan) View
    {
        get => (_zoomIdx, _pan);
        set
        {
            _zoomIdx = Math.Clamp(value.ZoomIndex, 0, ZoomLevels.Length - 1); // the zoom levels may have changed in settings
            _pan = value.Pan;
            RefreshCursorPosition();
            ZoomChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public void ResetView()
    {
        _pan = default;
        _zoomIdx = ZoomOne;
        RefreshCursorPosition();
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (_sprite is not { Width: > 0, Height: > 0 } || Bounds.Width < 16) return;
        double margin = 24;
        int best = 0;
        for (int i = 0; i < ZoomLevels.Length; i++)
            if (_sprite.Width * ZoomLevels[i] <= Bounds.Width - margin &&
                _sprite.Height * ZoomLevels[i] <= Bounds.Height - margin)
                best = i;
        _zoomIdx = best;
        _pan = default;
        RefreshCursorPosition();
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Moves the displayed sprite bounds to the canvas center without changing zoom.</summary>
    public void CenterImage()
    {
        if (_sprite is not { Width: > 0, Height: > 0 } || _bitmap is null) return;

        var image = SpriteRect();
        if (FlipH || FlipV || ElementScaleX != 1 || ElementScaleY != 1 || ElementAngle != 0)
        {
            var local = LocalSpriteRect();
            var transform = ElementTransform();
            Point[] corners =
            [
                transform.Transform(new Point(local.Left, local.Top)),
                transform.Transform(new Point(local.Right, local.Top)),
                transform.Transform(new Point(local.Left, local.Bottom)),
                transform.Transform(new Point(local.Right, local.Bottom)),
            ];
            double left = corners.Min(p => p.X), right = corners.Max(p => p.X);
            double top = corners.Min(p => p.Y), bottom = corners.Max(p => p.Y);
            image = new Rect(left, top, right - left, bottom - top);
        }

        var imageCenter = new Point(image.X + image.Width / 2, image.Y + image.Height / 2);
        var canvasCenter = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _pan += canvasCenter - imageCenter;
        RefreshCursorPosition();
        InvalidateVisual();
    }

    public void SelectBox(int index) => SelectBoxes(
        index >= 0 ? new[] { index } : Array.Empty<int>(), index);

    public void SelectBoxes(IEnumerable<int> indices, int active)
    {
        _selectedBoxes.Clear();
        foreach (int index in indices)
            if (index >= 0 && index < Boxes.Count) _selectedBoxes.Add(index);
        SelectedBox = _selectedBoxes.Contains(active) ? active
            : _selectedBoxes.Count > 0 ? _selectedBoxes.First() : -1;
        BoxSelected?.Invoke();
        InvalidateVisual();
    }


    /// <summary>Moves every selected box by whole sprite pixels.</summary>
    public void NudgeSelectedBoxes(int dx, int dy)
    {
        if (_selectedBoxes.Count == 0 || (dx == 0 && dy == 0)) return;
        _editingBoxes.Clear();
        _editingBoxes.UnionWith(_selectedBoxes);
        BoxEditStarted?.Invoke();
        foreach (int index in _editingBoxes)
        {
            if (index < 0 || index >= Boxes.Count) continue;
            var box = Boxes[index];
            box.X1 += dx; box.X2 += dx;
            box.Y1 += dy; box.Y2 += dy;
        }
        BoxChanged?.Invoke();
        BoxEditFinished?.Invoke();
        _editingBoxes.Clear();
        InvalidateVisual();
    }

    private Point Origin => new(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);

    private Rect SpriteRect()
    {
        if (_sprite is null || _bitmap is null) return default;
        double z = Zoom;
        var o = Origin;
        return new Rect(o.X + (ElementOffset.X - _sprite.AxisX) * z, o.Y + (ElementOffset.Y - _sprite.AxisY) * z,
                        _sprite.Width * z, _sprite.Height * z);
    }

    private bool SpriteContains(Point point)
    {
        if (_sprite is not { Width: > 0, Height: > 0 } || _bitmap is null) return false;
        if (!DragChangesOffset) return SpriteRect().Contains(point);
        if (ElementScaleX == 0 || ElementScaleY == 0) return false;
        return LocalSpriteRect().Contains(ElementTransform().Invert().Transform(point));
    }

    /// <summary>SpriteRect() without the element offset, for the transformed draw path.</summary>
    private Rect LocalSpriteRect()
    {
        if (_sprite is null || _bitmap is null) return default;
        double z = Zoom;
        var o = Origin;
        return new Rect(o.X - _sprite.AxisX * z, o.Y - _sprite.AxisY * z, _sprite.Width * z, _sprite.Height * z);
    }

    private Rect OnionRect()
    {
        if (_onionSprite is null || _onionBitmap is null) return default;
        double z = Zoom;
        var o = Origin;
        return new Rect(o.X + (_onionOffset.X - _onionSprite.AxisX) * z, o.Y + (_onionOffset.Y - _onionSprite.AxisY) * z,
                        _onionSprite.Width * z, _onionSprite.Height * z);
    }

    /// <summary>
    /// A single affine matrix combining flip, scale and rotation, pivoted on
    /// the sprite's own local axis — deliberately not the axis plus the
    /// element's offset. Those are two different things: flip/scale/rotate
    /// happen around the sprite's own anchor point, and the per-frame offset is
    /// a separate translation applied afterward. Folding the offset into the
    /// pivot instead would make rotation and flip drag the offset around too.
    /// The offset is added into this matrix's translation terms directly, after
    /// the pivot math, rather than baked into the pivot itself.
    /// </summary>
    private Matrix ElementTransform()
    {
        var pivot = Origin;
        double z = Zoom;
        double sx = ElementScaleX * (FlipH ? -1 : 1);
        double sy = ElementScaleY * (FlipV ? -1 : 1);
        // Engine angle is counter-clockwise; screen Y grows downward, so
        // negate before feeding the standard (screen-space) rotation matrix.
        double rad = -ElementAngle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        double a = cos * sx, b = sin * sx, c = -sin * sy, d = cos * sy;
        double e = pivot.X - (a * pivot.X + c * pivot.Y) + ElementOffset.X * z;
        double f = pivot.Y - (b * pivot.X + d * pivot.Y) + ElementOffset.Y * z;
        return new Matrix(a, b, c, d, e, f);
    }

    /// <summary>
    /// Reproduces render.go's renderWithBlending as closely as Avalonia's
    /// bitmap blend modes allow — see per-case comments for what's exact and
    /// what's a stand-in. Not engine-accurate: no PalFX, and no true
    /// arithmetic subtract (the engine gets that from GL_FUNC_REVERSE_SUBTRACT,
    /// a blend *equation* Avalonia doesn't expose as a bitmap blend *mode*).
    /// </summary>
    private void DrawBlended(DrawingContext ctx, bool transformed, Rect src, Rect dst)
    {
        void DrawBitmap(WriteableBitmap bmp)
        {
            if (transformed)
                // ElementTransform() supplies the offset itself, so the rect it
                // draws must exclude it here or the offset would apply twice.
                using (ctx.PushTransform(ElementTransform()))
                    ctx.DrawImage(bmp, src, LocalSpriteRect());
            else
                ctx.DrawImage(bmp, src, dst);
        }

        void DrawSprite() => DrawBitmap(_bitmap!);

        var blend = ElementBlend;
        if (blend.Mode == BlendMode.None) { DrawSprite(); return; }

        double srcW = Math.Clamp(blend.Source / 255.0, 0, 1);
        double dstW = Math.Clamp(blend.Destination / 255.0, 0, 1);

        switch (blend.Mode)
        {
            case BlendMode.Add:
                // Exact: render.go's Add/Add1/AddAlpha (same TT_add, different
                // Source/Destination weights) all reduce to dst*D/255 + src*S/255.
                // The dst*D/255 term is a black overlay at (1-D/255) opacity,
                // shaped to the sprite's own silhouette (not the full bounding
                // rect) so it darkens only where the sprite is actually opaque —
                // its transparent/masked pixels pass the background through
                // untouched, same trick renderWithBlending itself uses (a
                // BlendZero/OneMinusSrcAlpha pass).
                if (dstW < 1)
                    using (ctx.PushOpacity(1 - dstW))
                        DrawBitmap(SilhouetteBitmap);
                using (ctx.PushRenderOptions(default(RenderOptions) with { BitmapBlendingMode = BitmapBlendingMode.Plus }))
                using (ctx.PushOpacity(srcW))
                    DrawSprite();
                break;

            case BlendMode.Subtract:
                // Same silhouette-shaped dst*D/255 pre-scale as Add. The src*S/255
                // subtraction itself is a stand-in: Avalonia exposes no true
                // arithmetic-subtract blend mode, and Render() has no way to
                // read back what's already drawn to do the subtraction by hand.
                // Multiplying by the sprite's color-inverted copy instead of its
                // real colors approximates dst-src rather than just darkening —
                // against a light backdrop a green sprite reads back purple, the
                // same complementary-color result the real subtract gives.
                if (dstW < 1)
                    using (ctx.PushOpacity(1 - dstW))
                        DrawBitmap(SilhouetteBitmap);
                using (ctx.PushRenderOptions(default(RenderOptions) with { BitmapBlendingMode = BitmapBlendingMode.Multiply }))
                using (ctx.PushOpacity(srcW))
                    DrawBitmap(InvertedBitmap);
                break;

            case BlendMode.SubAdd:
                // Rough approximation only. The real SubAdd does a grayscale-
                // tinted subtract pass at dst weight, then an additive pass in
                // full color at src weight. Reproduced here without the
                // grayscale conversion or the dst-side silhouette pre-scale —
                // both dropped for this placeholder. The subtract half uses the
                // same inverted-color Multiply stand-in as plain Subtract, for
                // the same reason (see that case's comment).
                using (ctx.PushRenderOptions(default(RenderOptions) with { BitmapBlendingMode = BitmapBlendingMode.Multiply }))
                using (ctx.PushOpacity(dstW))
                    DrawBitmap(InvertedBitmap);
                using (ctx.PushRenderOptions(default(RenderOptions) with { BitmapBlendingMode = BitmapBlendingMode.Plus }))
                using (ctx.PushOpacity(srcW))
                    DrawSprite();
                break;
        }
    }

    /// <summary>
    /// Laid out as at 100% zoom with no pan (axis at the canvas center), then
    /// scaled by the zoom and/or moved with the pan when set to follow them.
    /// </summary>
    private void DrawBackgroundImage(DrawingContext ctx)
    {
        if (BackgroundImage is not { } bg) return;
        var img = bg.Image;
        double iw = img.PixelSize.Width, ih = img.PixelSize.Height;
        if (iw <= 0 || ih <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        // Center pixel on the axis; floor keeps pixels on the sprite's grid. Tiling
        // reuses this rect as the tile size and origin.
        var rect = new Rect(c.X - Math.Floor(iw / 2), c.Y - Math.Floor(ih / 2), iw, ih);
        rect = rect.Translate(new Vector(bg.Offset.X, bg.Offset.Y));
        double z = bg.FollowZoom ? Zoom : 1;
        var anchor = bg.FollowPan ? Origin : c; // Origin = center + pan
        rect = new Rect(anchor.X + (rect.X - c.X) * z, anchor.Y + (rect.Y - c.Y) * z, rect.Width * z, rect.Height * z);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using (ctx.PushOpacity(bg.Opacity))
        {
            if (bg.Tile)
            {
                using (ctx.PushClip(new Rect(Bounds.Size)))
                {
                    double x0 = rect.X + Math.Floor(-rect.X / rect.Width) * rect.Width;
                    double y0 = rect.Y + Math.Floor(-rect.Y / rect.Height) * rect.Height;
                    var src = new Rect(0, 0, iw, ih);
                    for (double y = y0; y < Bounds.Height; y += rect.Height)
                        for (double x = x0; x < Bounds.Width; x += rect.Width)
                            ctx.DrawImage(img, src, new Rect(x, y, rect.Width, rect.Height));
                }
            }
            else
                ctx.DrawImage(img, new Rect(0, 0, iw, ih), rect);
        }
    }

    private IBrush CheckerBrush(int size)
    {
        if (_checkerBrush is not null && _checkerBrushSize == size && ReferenceEquals(_checkerBrushColor, CheckerDark))
            return _checkerBrush;

        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing
                {
                    Brush = CheckerDark,
                    Geometry = new RectangleGeometry(new Rect(0, 0, size, size)),
                },
                new GeometryDrawing
                {
                    Brush = CheckerDark,
                    Geometry = new RectangleGeometry(new Rect(size, size, size, size)),
                },
            },
        };
        _checkerBrush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, size * 2, size * 2, RelativeUnit.Absolute),
        };
        _checkerBrushColor = CheckerDark;
        _checkerBrushSize = size;
        return _checkerBrush;
    }

    private void DrawCrosshair(DrawingContext ctx, Point o)
    {
        var ch = Crosshair;
        double s = ch.ScaleWithZoom ? Zoom : 1;
        double t = ch.Thickness * s;
        if (FullCrosshair)
        {
            var guide = new Pen(new SolidColorBrush(ch.Color, GuideOpacity), t);
            ctx.DrawLine(guide, new Point(0, o.Y), new Point(Bounds.Width, o.Y));
            ctx.DrawLine(guide, new Point(o.X, 0), new Point(o.X, Bounds.Height));
        }
        if (ch.Image is { } img)
        {
            double w = img.PixelSize.Width, h = img.PixelSize.Height;
            ctx.DrawImage(img, new Rect(0, 0, w, h), new Rect(o.X - ch.Hotspot.X * s, o.Y - ch.Hotspot.Y * s, w * s, h * s));
            return;
        }
        var pen = new Pen(new SolidColorBrush(ch.Color), t);
        double a = ch.ArmLength * s;
        ctx.DrawLine(pen, new Point(o.X - a, o.Y), new Point(o.X + a, o.Y));
        ctx.DrawLine(pen, new Point(o.X, o.Y - a), new Point(o.X, o.Y + a));
    }

    public override void Render(DrawingContext ctx)
    {
        var full = new Rect(Bounds.Size);
        ctx.FillRectangle(Background, full);

        if (Checkerboard)
        {
            // Screen space, so the squares keep their size as the canvas zooms; clamp hand-edited values.
            int size = Math.Clamp(CheckerSize, 1, 512);
            ctx.FillRectangle(CheckerBrush(size), full);
        }

        DrawBackgroundImage(ctx);

        if (ShowOnionSkin && _onionBitmap is not null && _onionSprite is not null)
        {
            using (ctx.PushOpacity(OnionOpacityOverride ?? OnionOpacity))
                ctx.DrawImage(_onionBitmap, new Rect(0, 0, _onionSprite.Width, _onionSprite.Height), OnionRect());
        }

        if (_bitmap is not null && _sprite is not null)
        {
            var dst = SpriteRect();
            var src = new Rect(0, 0, _sprite.Width, _sprite.Height);

            bool transformed = FlipH || FlipV || ElementScaleX != 1 || ElementScaleY != 1 || ElementAngle != 0;
            DrawBlended(ctx, transformed, src, dst);

            if (ShowBounds) ctx.DrawRectangle(EdgePen, dst);
        }

        if (ShowOnionSkin)
            foreach (var g in _ghostBoxes)
                ctx.DrawRectangle(new Pen(new SolidColorBrush(g.Color, OnionOpacityOverride ?? OnionOpacity), 1,
                    new DashStyle([4, 3], 0)), ScreenRect(g));

        var o = Origin;
        for (int i = 0; i < Boxes.Count; i++)
        {
            var box = Boxes[i];
            var r = ScreenRect(box);
            bool selected = _selectedBoxes.Contains(i);
            bool active = i == SelectedBox;

            ctx.FillRectangle(new SolidColorBrush(box.Color, selected ? 0.38 : 0.22), r);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(box.Color), active ? 3 : selected ? 2 : 1), r);
            DrawIndexLabel(ctx, r, box.Index, box.Color);

            if (!active) continue;
            foreach (var h in Handles(r))
                ctx.DrawRectangle(Brushes.White, new Pen(Brushes.Black), h);
        }

        if (ShowCrosshair) DrawCrosshair(ctx, o);

        if (_selection is { } selection && _sprite is not null && PaintMode && ActiveTool == PaintTool.Selection)
        {
            var sr = SpriteRect();
            var white = new Pen(Brushes.White, 1, new DashStyle([3, 3], 0));
            var black = new Pen(Brushes.Black, 1, new DashStyle([3, 3], 3));
            if (selection.Mask is null)
            {
                var rect = new Rect(sr.X + selection.X * Zoom, sr.Y + selection.Y * Zoom,
                    selection.Width * Zoom, selection.Height * Zoom);
                ctx.DrawRectangle(white, rect);
                ctx.DrawRectangle(black, rect);
            }
            else
            {
                if (!ReferenceEquals(_outlineMask, selection.Mask))
                {
                    _outlineRuns = OutlineRuns(selection);
                    _outlineMask = selection.Mask;
                }
                var outline = new StreamGeometry();
                using (var g = outline.Open())
                    foreach (var (x1, y1, x2, y2) in _outlineRuns)
                    {
                        g.BeginFigure(new Point(sr.X + (selection.X + x1) * Zoom, sr.Y + (selection.Y + y1) * Zoom), false);
                        g.LineTo(new Point(sr.X + (selection.X + x2) * Zoom, sr.Y + (selection.Y + y2) * Zoom));
                        g.EndFigure(false);
                    }
                ctx.DrawGeometry(null, white, outline);
                ctx.DrawGeometry(null, black, outline);
            }
        }

        // An armed replace slot means the next click is a color pick, not a stamp, so no brush square.
        if (PaintMode && !ArmedForExternalPick && ActiveTool is PaintTool.Pencil or PaintTool.Eraser or PaintTool.Replace
            && _hoverPos is { } hp && TryPixelAt(hp, out int hx, out int hy))
        {
            var sr = SpriteRect();
            double z = Zoom;
            int half = BrushSize / 2;
            var rect = new Rect(sr.X + (hx - half) * z, sr.Y + (hy - half) * z, BrushSize * z, BrushSize * z);
            ctx.DrawRectangle(BrushOutlineBase, rect);
            bool inverse = ActiveTool == PaintTool.Replace && (_painting ? _inverseStroke : _inverseHeld);
            ctx.DrawRectangle(inverse ? InverseOutlineDash() : BrushOutlineDash, rect);
        }
    }

    /// <summary>The brush outline's dashes in the theme accent while Replace runs New → Old.</summary>
    private IPen InverseOutlineDash() =>
        new Pen(this.TryFindResource("ThemeAccent", out var v) && v is IBrush b ? b : Brushes.White, 1, new DashStyle([2, 2], 0));

    private void SetInverseHeld(bool held)
    {
        if (_inverseHeld == held) return;
        _inverseHeld = held;
        if (PaintMode && ActiveTool == PaintTool.Replace) InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SetInverseHeld(true);
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SetInverseHeld(false);
        base.OnKeyUp(e);
    }

    private static void DrawIndexLabel(DrawingContext ctx, Rect r, int index, Color color)
    {
        var text = new FormattedText(index.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, new SolidColorBrush(color));
        ctx.DrawText(text, new Point(r.X + 2, r.Y + 1));
    }

    private Rect ScreenRect(BoxOverlay b)
    {
        var o = Origin;
        double z = Zoom;
        return new Rect(o.X + b.Left * z, o.Y + b.Top * z, b.Right * z - b.Left * z, b.Bottom * z - b.Top * z);
    }

    private static IEnumerable<Rect> Handles(Rect r)
    {
        foreach (var (x, y) in Corners(r))
            yield return new Rect(x - HandleSize / 2.0, y - HandleSize / 2.0, HandleSize, HandleSize);
    }

    private static IEnumerable<(double X, double Y)> Corners(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (r.X, r.Y); yield return (cx, r.Y); yield return (r.Right, r.Y);
        yield return (r.X, cy); yield return (r.Right, cy);
        yield return (r.X, r.Bottom); yield return (cx, r.Bottom); yield return (r.Right, r.Bottom);
    }

    private static readonly BoxGrip[] GripOrder =
    [
        BoxGrip.TopLeft, BoxGrip.Top, BoxGrip.TopRight,
        BoxGrip.Left, BoxGrip.Right,
        BoxGrip.BottomLeft, BoxGrip.Bottom, BoxGrip.BottomRight,
    ];

    /// <summary>
    /// What is under the pointer: a handle of the selected box first, since
    /// handles sit on the edges and would otherwise be unreachable, then the
    /// smallest box body under the point — a big background box would
    /// otherwise shadow a small one forever. Topmost wins size ties.
    /// </summary>
    private (int Index, BoxGrip Grip) HitTest(Point p)
    {
        if (SelectedBox >= 0 && SelectedBox < Boxes.Count)
        {
            var r = ScreenRect(Boxes[SelectedBox]);
            int k = 0;
            foreach (var h in Handles(r))
            {
                if (h.Contains(p)) return (SelectedBox, GripOrder[k]);
                k++;
            }
        }
        int best = -1;
        double bestArea = double.MaxValue;
        for (int i = Boxes.Count - 1; i >= 0; i--)
        {
            var r = ScreenRect(Boxes[i]);
            if (!r.Contains(p)) continue;
            double area = r.Width * r.Height;
            if (area < bestArea) { bestArea = area; best = i; }
        }
        return (best, best >= 0 ? BoxGrip.Body : BoxGrip.None);
    }

    /// <summary>Sprite-local pixel under a screen point, or false if outside the sprite/canvas.</summary>
    private bool TryPixelAt(Point p, out int px, out int py)
    {
        px = py = 0;
        if (_sprite is not { Width: > 0, Height: > 0 }) return false;
        var r = SpriteRect();
        if (!r.Contains(p)) return false;
        px = (int)((p.X - r.X) / Zoom);
        py = (int)((p.Y - r.Y) / Zoom);
        return px >= 0 && px < _sprite.Width && py >= 0 && py < _sprite.Height;
    }

    /// <summary>Eyedropper pick at a screen point, regardless of the active tool — used by the right-click context menu.</summary>
    public void PickColorAt(Point p)
    {
        if (_sprite is not { } sp) return;
        if (!TryPixelAt(p, out int x, out int y)) return;
        int i = y * sp.Width + x;

        if (sp.Indexed is { } idx)
        {
            PaintColorIndex = idx[i];
            ColorPicked?.Invoke(PaintColorIndex);
            return;
        }
        // True color has no index to report, so hand back the pixel. Direct is BGRA, the same
        // order ScratchImageView reads; it also follows SharesWith, so a linked sprite is fine.
        if (sp.Direct is { } bgra && i * 4 + 3 < bgra.Length)
        {
            int off = i * 4;
            ColorPickedValue?.Invoke(new Rgba(bgra[off + 2], bgra[off + 1], bgra[off], bgra[off + 3]));
        }
    }

    /// <summary>Runs the active paint tool at a screen point, on an indexed sprite only.</summary>
    private void ApplyTool(Point p)
    {
        if (ReadOnly) return; // belt-and-braces: OnPointerPressed already can't reach this when ReadOnly
        if (_sprite?.Indexed is not { } flattened) return;
        if (!TryPixelAt(p, out int x, out int y)) return;
        int i = y * _sprite.Width + x;

        if (ActiveTool == PaintTool.Eyedropper)
        {
            PaintColorIndex = flattened[i];
            ColorPicked?.Invoke(PaintColorIndex);
            return;
        }

        byte[] idx = PaintTarget ?? flattened;
        byte target = (byte)PaintColorIndex;
        bool changed = ActiveTool switch
        {
            PaintTool.Pencil => PaintBrush(idx, x, y, _ => target),
            PaintTool.Eraser => PaintBrush(idx, x, y, _ => Sprite.TransparentIndex),
            PaintTool.Fill => FloodFill(idx, x, y, target),
            PaintTool.Replace => PaintBrush(idx, x, y,
                v => (_inverseStroke ? InverseReplaceMap : ReplaceMap) is { } m && m.TryGetValue(v, out byte to) ? to : v),
            _ => false,
        };
        if (!changed) return;

        _sprite.PixelsDirty = true;
        PixelsPainted?.Invoke();
        if (_bitmapCache?.Remove((_sprite, _palette, _showMask)) == true) _bitmapCached = false; // stale now
        RefreshBitmap();
        InvalidateVisual();
    }

    /// <summary>
    /// Stamps a BrushSize x BrushSize square centered on the clicked pixel,
    /// clipped to the sprite. Each covered pixel's new value comes from
    /// <paramref name="map"/> applied to its own current value, so Pencil/Eraser
    /// (which ignore the input and return a fixed value) and Replace (which
    /// only changes values it recognizes) share the same stamping logic.
    /// BrushSize 1 is a single pixel, same as before brush size existed.
    /// </summary>
    private bool PaintBrush(byte[] idx, int cx, int cy, Func<byte, byte> map)
    {
        int w = _sprite!.Width, h = _sprite.Height;
        int half = BrushSize / 2;
        bool changed = false;
        for (int y = cy - half; y < cy - half + BrushSize; y++)
        {
            if (y < 0 || y >= h) continue;
            for (int x = cx - half; x < cx - half + BrushSize; x++)
            {
                if (x < 0 || x >= w) continue;
                int i = y * w + x;
                byte newVal = map(idx[i]);
                if (idx[i] == newVal) continue;
                idx[i] = newVal;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>4-connected flood fill of the region matching the start pixel's index. BrushSize does not apply here.</summary>
    private bool FloodFill(byte[] idx, int startX, int startY, byte replacement)
    {
        int w = _sprite!.Width, h = _sprite.Height;
        byte target = idx[startY * w + startX];
        if (target == replacement) return false;

        var stack = new Stack<(int X, int Y)>();
        stack.Push((startX, startY));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || x >= w || y < 0 || y >= h) continue;
            int i = y * w + x;
            if (idx[i] != target) continue;
            idx[i] = replacement;
            stack.Push((x + 1, y)); stack.Push((x - 1, y));
            stack.Push((x, y + 1)); stack.Push((x, y - 1));
        }
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);

        if (p.Properties.IsMiddleButtonPressed) { _pan = default; InvalidateVisual(); return; }

        if (p.Properties.IsRightButtonPressed)
        {
            var (index, _) = Boxes.Count > 0 ? HitTest(p.Position) : (-1, BoxGrip.None);
            if (index >= 0) SelectBox(index);
            // Raised either way, with -1 meaning "the canvas itself, not a box",
            // so the owner can still offer something (even just a placeholder)
            // on a right-click that didn't land on anything.
            BoxContextRequested?.Invoke(index, p.Position);
            return;
        }
        if (!p.Properties.IsLeftButtonPressed) return;

        // Ahead of the ReadOnly branch, not after it: an armed external pick is a pick
        // whatever the view would otherwise allow, and PalettePreview is ReadOnly, so
        // its pan-only branch would swallow the click and ColorPicked would never raise.
        // SpriteCanvas is not ReadOnly, so moving this up changes nothing there.
        if (ArmedForExternalPick)
        {
            ArmedForExternalPick = false;
            if (_bitmap is not null && SpriteRect().Contains(p.Position)) PickColorAt(p.Position);
            return;
        }

        if (ReadOnly)
        {
            // Skips every mutating path below entirely — no box hit-test, no
            // paint/selection start, no axis/offset drag start. A left-drag is
            // always just a pan, full stop.
            _dragFrom = p.Position;
            _panning = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        _dragFrom = p.Position;

        // Handles belong only to the active box. Ctrl/Shift-click changes the set
        // without starting a drag; an ordinary click on a selected box keeps
        // the group intact so its body can move the whole selection.
        (int Index, BoxGrip Grip) hit = Boxes.Count > 0 ? HitTest(p.Position) : (-1, BoxGrip.None);
        _boxPressIndex = hit.Index;
        _boxDragMoved = false;
        // Shift with Replace paints New → Old instead (see ApplyTool).
        bool replacing = PaintMode && ActiveTool == PaintTool.Replace;
        bool extendSelection = (e.KeyModifiers & (replacing ? KeyModifiers.Control : KeyModifiers.Control | KeyModifiers.Shift)) != 0;
        if (extendSelection)
        {
            if (hit.Index >= 0)
            {
                if (_selectedBoxes.Contains(hit.Index))
                {
                    _selectedBoxes.Remove(hit.Index);
                    if (SelectedBox == hit.Index)
                        SelectedBox = _selectedBoxes.Count > 0 ? _selectedBoxes.Max() : -1;
                }
                else
                {
                    _selectedBoxes.Add(hit.Index);
                    SelectedBox = hit.Index;
                }
                BoxSelected?.Invoke();
                InvalidateVisual();
            }
            return;
        }

        if (hit.Index < 0)
        {
            SelectBox(-1);
        }
        else if (!_selectedBoxes.Contains(hit.Index))
        {
            SelectBox(hit.Index);
        }
        else if (hit.Grip != BoxGrip.Body)
        {
            SelectedBox = hit.Index;
            BoxSelected?.Invoke();
        }
        _grip = hit.Grip;
        if (_grip != BoxGrip.None)
        {
            _editingBoxes.Clear();
            if (_grip == BoxGrip.Body) _editingBoxes.UnionWith(_selectedBoxes);
            else if (SelectedBox >= 0) _editingBoxes.Add(SelectedBox);
            BoxEditStarted?.Invoke();
            Cursor = new Cursor(_grip == BoxGrip.Body ? StandardCursorType.SizeAll : StandardCursorType.Cross);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        bool onSprite = SpriteContains(p.Position);
        if (PaintMode && ActiveTool == PaintTool.Selection && onSprite)
        {
            if (_selection is { } existing && PixelRect(existing).Contains(p.Position)
                && TryPixelAt(p.Position, out int hitX, out int hitY) && existing.Contains(hitX - existing.X, hitY - existing.Y))
            {
                _movingSelection = true;
                _selectionMoveOriginal = existing;
                _selectionMoveStart = p.Position;
                _selectPressPos = p.Position;
                _selectDragged = false;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Pointer.Capture(this);
                return;
            }

            _selecting = true;
            SelectionStarted?.Invoke();
            _selectionStart = p.Position;
            _selectPressPos = p.Position;
            _selectDragged = false;
            _selection = null; // nothing is selected until the press becomes a drag
            if (LassoSelect) StartLasso(p.Position);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        if (PaintMode && onSprite)
        {
            // A pick moves the color and nothing else, so it runs the same press/move/release
            // path (which keeps drag-picking continuous) but reports no stroke: raising one
            // would open a pending paint session and mark the file changed for no edit.
            _pickingStroke = ActiveTool == PaintTool.Eyedropper;
            if (!_pickingStroke && PaintStrokeAllowed?.Invoke() == false) { _pickingStroke = false; return; }
            _painting = true;
            _inverseStroke = ActiveTool == PaintTool.Replace && (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (!_pickingStroke) PaintStrokeStarted?.Invoke();
            if (_inverseStroke) InverseReplaceStarted?.Invoke();
            ApplyTool(p.Position);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        _draggingSprite = onSprite && (!DragChangesOffset || OffsetDraggingEnabled);
        _panning = !onSprite || (DragChangesOffset && !OffsetDraggingEnabled);
        _clearSelectionOnClick = _panning && !onSprite && PaintMode && ActiveTool == PaintTool.Selection && _selection is not null;
        _selectPressPos = p.Position;
        if (_draggingSprite)
        {
            if (DragChangesOffset) OffsetDragStarted?.Invoke();
            else AxisDragStarted?.Invoke();
        }
        Cursor = new Cursor(_draggingSprite ? StandardCursorType.SizeAll : StandardCursorType.Hand);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private Point? _lastMovePos;
    private DispatcherTimer? _tipTimer;

    /// <summary>Reopens the tip once the cursor rests; Avalonia only opens it once per hover.</summary>
    private void RestartTipTimer()
    {
        if (_tipTimer is null)
        {
            _tipTimer = new DispatcherTimer();
            _tipTimer.Tick += OnTipTimer;
        }
        _tipTimer.Stop();
        _tipTimer.Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(this));
        _tipTimer.Start();
    }

    /// <summary>The tip is a pick hint, so only over an indexed sprite's pixels, where a pick selects a color.</summary>
    private bool TipAllowedHere() =>
        _sprite?.Indexed is not null && _lastMovePos is { } p && TryPixelAt(p, out _, out _);

    private void OnTipTimer(object? sender, EventArgs e)
    {
        _tipTimer?.Stop();
        if (IsPointerOver && !_panning && !_draggingSprite && !_painting) ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // Hints are for a resting cursor: a move hides the tip, and it returns after a pause.
        var movedTo = e.GetPosition(this);
        if (ToolTip.GetTip(this) is not null && movedTo != _lastMovePos)
        {
            if (ToolTip.GetIsOpen(this)) ToolTip.SetIsOpen(this, false);
            RestartTipTimer();
        }
        _lastMovePos = movedTo;

        _cursorPosition = e.GetPosition(this);
        RefreshCursorPosition();

        if (PaintMode && ActiveTool is PaintTool.Pencil or PaintTool.Eraser or PaintTool.Replace)
        {
            _inverseHeld = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            _hoverPos = e.GetPosition(this);
            InvalidateVisual(); // the outline needs to follow the cursor even when not painting
        }

        if ((_movingSelection || _selecting) && !_selectDragged)
        {
            if (Point.Distance(e.GetPosition(this), _selectPressPos) <= SelectDragThreshold) return;
            _selectDragged = true;
        }
        if (_movingSelection)
        {
            MoveSelection(e.GetPosition(this));
            return;
        }
        if (_selecting)
        {
            if (_lassoPoints is not null) AddLassoPoint(e.GetPosition(this));
            else UpdateSelection(e.GetPosition(this));
            return;
        }
        if (_painting) { ApplyTool(e.GetPosition(this)); return; }
        if (_grip != BoxGrip.None) { DragBox(e.GetPosition(this)); return; }
        if (!_panning && !_draggingSprite) return;

        var p = e.GetPosition(this);
        var d = p - _dragFrom;

        if (_panning)
        {
            if (_clearSelectionOnClick && Point.Distance(p, _selectPressPos) > SelectDragThreshold) _clearSelectionOnClick = false;
            _pan += d;
            _dragFrom = p;
            RefreshCursorPosition();
            InvalidateVisual();
            return;
        }

        // Moving the sprite right means its axis moves left within the bitmap
        // (axis mode); in offset mode it means the offset simply increases,
        // matching drag direction, since offset shifts the draw position directly.
        int dx = (int)(d.X / Zoom), dy = (int)(d.Y / Zoom);
        if (dx == 0 && dy == 0) return;

        if (DragChangesOffset)
        {
            OffsetDelta?.Invoke(dx, dy);
            RefreshCursorPosition();
        }
        else if (_sprite is not null && !ReadOnly) // belt-and-braces: _draggingSprite can't be true here when ReadOnly
        {
            _sprite.AxisX -= dx;
            _sprite.AxisY -= dy;
            AxisChanged?.Invoke();
        }
        _dragFrom = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_movingSelection)
        {
            _movingSelection = false;
            if (_selectDragged) { SelectionMoved?.Invoke(); SelectionMoveFinished?.Invoke(); }
            else ClearSelectionByClick();
            Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            return;
        }
        if (_selecting)
        {
            _selecting = false;
            if (!_selectDragged)
            {
                _lassoPoints = null;
                ClearSelectionByClick();
            }
            else if (_lassoPoints is not null)
            {
                AddLassoPoint(e.GetPosition(this));
                _lassoPoints = null;
                SelectionChanged?.Invoke(); // also when the lasso enclosed nothing and cleared the selection
            }
            else
            {
                UpdateSelection(e.GetPosition(this));
                if (_selection is { Width: > 0, Height: > 0 }) SelectionChanged?.Invoke();
            }
            Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            return;
        }
        if (_painting)
        {
            _painting = false;
            if (_pickingStroke) _pickingStroke = false;
            else PaintStrokeFinished?.Invoke();
            Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            return;
        }
        if (_draggingSprite)
        {
            if (DragChangesOffset) OffsetDragFinished?.Invoke();
            else AxisDragFinished?.Invoke();
        }
        if (_grip != BoxGrip.None)
        {
            // Playback can swap out the whole Boxes list mid-drag (advancing
            // to a new frame reassigns it), leaving SelectedBox pointing past
            // whatever's here now — this crashed before the bounds check.
            if (SelectedBox >= 0 && SelectedBox < Boxes.Count) Boxes[SelectedBox].Normalize();
            BoxEditFinished?.Invoke();
            _editingBoxes.Clear();
            if (_grip == BoxGrip.Body && !_boxDragMoved
                && _boxPressIndex >= 0 && _boxPressIndex < Boxes.Count)
                SelectBox(_boxPressIndex);
            _boxPressIndex = -1;
            _grip = BoxGrip.None;
        }
        if (_clearSelectionOnClick)
        {
            _clearSelectionOnClick = false;
            ClearSelectionByClick();
        }
        _panning = _draggingSprite = false;
        Cursor = Cursor.Default;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Applies a drag to the selected box. Movement is quantised to whole
    /// sprite pixels, so a box never lands on a fractional coordinate the
    /// format cannot store.
    /// </summary>
    private void DragBox(Point p)
    {
        if (SelectedBox < 0 || SelectedBox >= Boxes.Count) return;

        var d = p - _dragFrom;
        int dx = (int)Math.Round(d.X / Zoom), dy = (int)Math.Round(d.Y / Zoom);
        if (dx == 0 && dy == 0) return;
        _boxDragMoved = true;

        if (_grip == BoxGrip.Body)
        {
            foreach (int index in _selectedBoxes)
            {
                if (index < 0 || index >= Boxes.Count) continue;
                var selected = Boxes[index];
                selected.X1 += dx; selected.X2 += dx;
                selected.Y1 += dy; selected.Y2 += dy;
            }
            _dragFrom = new Point(_dragFrom.X + dx * Zoom, _dragFrom.Y + dy * Zoom);
            BoxChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        var b = Boxes[SelectedBox];
        int left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
        switch (_grip)
        {
            case BoxGrip.Left: left += dx; break;
            case BoxGrip.Right: right += dx; break;
            case BoxGrip.Top: top += dy; break;
            case BoxGrip.Bottom: bottom += dy; break;
            case BoxGrip.TopLeft: left += dx; top += dy; break;
            case BoxGrip.TopRight: right += dx; top += dy; break;
            case BoxGrip.BottomLeft: left += dx; bottom += dy; break;
            case BoxGrip.BottomRight: right += dx; bottom += dy; break;
        }
        (b.X1, b.Y1, b.X2, b.Y2) = (left, top, right, bottom);
        _dragFrom = new Point(_dragFrom.X + dx * Zoom, _dragFrom.Y + dy * Zoom);

        BoxChanged?.Invoke();
        InvalidateVisual();
    }

    private Rect PixelRect(PixelSelection selection)
    {
        var sr = SpriteRect();
        return new Rect(sr.X + selection.X * Zoom, sr.Y + selection.Y * Zoom,
            selection.Width * Zoom, selection.Height * Zoom);
    }

    /// <summary>
    /// Moves the selection by the pointer's pixel offset. Unlike hit-testing, this
    /// works past the sprite's edge, so a pasted block can be dragged off the canvas.
    /// </summary>
    private void MoveSelection(Point point)
    {
        if (_sprite is not { Width: > 0, Height: > 0 }) return;
        var r = SpriteRect();
        int dx = (int)Math.Floor((point.X - r.X) / Zoom) - (int)Math.Floor((_selectionMoveStart.X - r.X) / Zoom);
        int dy = (int)Math.Floor((point.Y - r.Y) / Zoom) - (int)Math.Floor((_selectionMoveStart.Y - r.Y) / Zoom);
        var original = _selectionMoveOriginal;
        int nx = original.X + dx;
        int ny = original.Y + dy;
        _selection = original with { X = nx, Y = ny };
        SelectionMoved?.Invoke();
        InvalidateVisual();
    }

    private void UpdateSelection(Point point)
    {
        if (_sprite is not { Width: > 0, Height: > 0 }) return;
        var r = SpriteRect();
        var clamped = new Point(Math.Clamp(point.X, r.Left, Math.Max(r.Left, r.Right - 0.001)),
            Math.Clamp(point.Y, r.Top, Math.Max(r.Top, r.Bottom - 0.001)));
        if (!TryPixelAt(clamped, out int x, out int y)) return;
        if (!TryPixelAt(_selectionStart, out int sx, out int sy)) return;
        int left = Math.Min(sx, x), top = Math.Min(sy, y);
        _selection = new PixelSelection(left, top, Math.Abs(x - sx) + 1, Math.Abs(y - sy) + 1);
        InvalidateVisual();
    }

    /// <summary>A click clears the selection and settles a floating paste, like starting a new one.</summary>
    private void ClearSelectionByClick()
    {
        SelectionStarted?.Invoke();
        _selection = null;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    private void StartLasso(Point point)
    {
        _lassoPoints = [];
        AddLassoPoint(point);
    }

    /// <summary>Adds a screen point to the lasso, clamped to the sprite, and rebuilds the selection from the closed path.</summary>
    private void AddLassoPoint(Point point)
    {
        if (_lassoPoints is null || _sprite is not { Width: > 0, Height: > 0 } sprite) return;
        var r = SpriteRect();
        var local = new Point(Math.Clamp((point.X - r.X) / Zoom, 0, sprite.Width), Math.Clamp((point.Y - r.Y) / Zoom, 0, sprite.Height));
        if (_lassoPoints.Count > 0 && _lassoPoints[^1] == local) return;
        _lassoPoints.Add(local);
        _selection = LassoSelection(_lassoPoints, sprite.Width, sprite.Height);
        InvalidateVisual();
    }

    /// <summary>
    /// Pixels whose centers lie inside the closed path (nonzero winding, so a looped
    /// outline stays filled), cropped to their bounds; null when none do.
    /// </summary>
    private static PixelSelection? LassoSelection(List<Point> points, int width, int height)
    {
        if (points.Count < 3) return null;
        var full = new bool[width * height];
        int minX = width, minY = height, maxX = -1, maxY = -1;
        var crossings = new List<(double X, int Dir)>();
        for (int py = 0; py < height; py++)
        {
            double cy = py + 0.5;
            crossings.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if ((a.Y <= cy) == (b.Y <= cy)) continue;
                crossings.Add((a.X + (cy - a.Y) * (b.X - a.X) / (b.Y - a.Y), b.Y > a.Y ? 1 : -1));
            }
            if (crossings.Count < 2) continue;
            crossings.Sort((l, r) => l.X.CompareTo(r.X));
            int winding = 0;
            for (int i = 0; i < crossings.Count - 1; i++)
            {
                winding += crossings[i].Dir;
                if (winding == 0) continue;
                int from = Math.Max(0, (int)Math.Ceiling(crossings[i].X - 0.5));
                int to = Math.Min(width, (int)Math.Ceiling(crossings[i + 1].X - 0.5));
                if (from >= to) continue;
                Array.Fill(full, true, py * width + from, to - from);
                minX = Math.Min(minX, from);
                maxX = Math.Max(maxX, to - 1);
                minY = Math.Min(minY, py);
                maxY = py;
            }
        }
        if (maxX < 0) return null;
        int w = maxX - minX + 1, h = maxY - minY + 1;
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(full, (minY + y) * width + minX, mask, y * w, w);
        return new PixelSelection(minX, minY, w, h, mask);
    }

    /// <summary>Edges between selected and unselected pixels, merged into straight runs, relative to the selection's X and Y.</summary>
    private static List<(int X1, int Y1, int X2, int Y2)> OutlineRuns(PixelSelection s)
    {
        var runs = new List<(int X1, int Y1, int X2, int Y2)>();
        for (int j = 0; j <= s.Height; j++)
        {
            int start = -1;
            for (int x = 0; x <= s.Width; x++)
            {
                bool edge = x < s.Width && s.Contains(x, j - 1) != s.Contains(x, j);
                if (edge && start < 0) start = x;
                else if (!edge && start >= 0) { runs.Add((start, j, x, j)); start = -1; }
            }
        }
        for (int i = 0; i <= s.Width; i++)
        {
            int start = -1;
            for (int y = 0; y <= s.Height; y++)
            {
                bool edge = y < s.Height && s.Contains(i - 1, y) != s.Contains(i, y);
                if (edge && start < 0) start = y;
                else if (!edge && start >= 0) { runs.Add((i, start, i, y)); start = -1; }
            }
        }
        return runs;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _cursorPosition = e.GetPosition(this);
        StepZoom(e.Delta.Y > 0 ? 1 : -1); // StepZoom already raises ZoomChanged
        e.Handled = true;
    }

    private void RefreshCursorPosition()
    {
        if (_cursorPosition is not { } point || !Bounds.Contains(point)
            || (DragChangesOffset && (ElementScaleX == 0 || ElementScaleY == 0)))
        {
            CursorPositionCleared?.Invoke();
            return;
        }

        if (DragChangesOffset)
            point = ElementTransform().Invert().Transform(point);
        CursorPositionChanged?.Invoke(
            (int)Math.Floor((point.X - Origin.X) / Zoom),
            (int)Math.Floor((point.Y - Origin.Y) / Zoom));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tipTimer?.Stop();
        _lastMovePos = null;
        _cursorPosition = null;
        CursorPositionCleared?.Invoke();
        if (_hoverPos is null) return;
        _hoverPos = null;
        InvalidateVisual();
    }
}
