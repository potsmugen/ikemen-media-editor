using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Snd;

namespace Editor;

/// <summary>
/// A quick min/max amplitude envelope for one WAV, one column per pixel.
/// Reads PCM straight out of the stored WAV bytes using the offset/format
/// SndReader already found, so no decoding library is needed. Stereo (and
/// beyond) draws channel 0 on top and channel 1 on the bottom, the same
/// top/bottom split convention the Timeline uses for Clsn1/Clsn2 — channels
/// past the second aren't drawn; this is "is there sound here, and roughly
/// what shape", not a mastering tool. Dragging selects a frame range (shown as a
/// shaded band); a plain click clears it. The owner keeps the selection state.
/// Zoom: the whole sound fits until zoomed; the wheel zooms around the cursor,
/// Shift+wheel scrolls. A new sound fits; the same sound after an edit keeps
/// zoom and position.
/// </summary>
public class WaveformView : Control
{
    private SoundEntry? _entry;
    private float[]? _minsL, _maxsL, _minsR, _maxsR;
    private (double Width, double Start, double Spp, bool Stereo, byte[]? Data) _envelopeKey;

    private double? _samplesPerPixel; // null = fit the whole sound
    private double _viewStart;        // first visible frame

    public WaveformView()
    {
        MinHeight = 40;
        ClipToBounds = true;
    }

    /// <summary>Backdrop color; kept in step with the app theme (see MainWindow.ApplyTheme).</summary>
    public IBrush Background { get; set; } = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28));

    /// <summary>Deepest zoom, in pixels per sample (settings.json WaveformMaxPixelsPerSample).</summary>
    public double MaxPixelsPerSample { get; set; } = Settings.DefaultWaveformMaxPixelsPerSample;
    /// <summary>Zoom factor per wheel notch (settings.json WaveformZoomStep).</summary>
    public double ZoomStep { get; set; } = Settings.DefaultWaveformZoomStep;
    /// <summary>Shift+wheel scroll per notch, as a fraction of the visible width (settings.json WaveformScrollStep).</summary>
    public double ScrollStep { get; set; } = Settings.DefaultWaveformScrollStep;

    /// <summary>Raised when zoom, scroll position or the sound changes, so a scrollbar can follow.</summary>
    public event Action? ViewChanged;

    public int TotalFrames => _entry is null ? 0 : SndEdit.FrameCount(_entry);
    private double Width1 => Math.Max(1, Bounds.Width);
    private double FitSpp => TotalFrames / Width1;
    private double MinSpp => 1 / Math.Max(MaxPixelsPerSample, 1e-9);
    /// <summary>Frames per pixel right now.</summary>
    public double SamplesPerPixel => _samplesPerPixel ?? FitSpp;
    public double VisibleFrames => SamplesPerPixel * Width1;

    /// <summary>First visible frame. Setting it scrolls (clamped).</summary>
    public double ViewStart
    {
        get => _viewStart;
        set => SetView(_samplesPerPixel, value);
    }

    /// <summary>Selected frames [Start, End), drawn as a shaded band. Set by the owner; dragging also sets it.</summary>
    public (int Start, int End)? Selection
    {
        get => _selection;
        set { _selection = value; InvalidateVisual(); }
    }
    private (int Start, int End)? _selection;

    /// <summary>Playback position: a line marks Frame. Null hides it.</summary>
    public (int Start, double Frame)? Playhead
    {
        get => _playhead;
        set { _playhead = value; InvalidateVisual(); }
    }
    private (int Start, double Frame)? _playhead;

    /// <summary>Pixels the pointer must move before a press counts as a drag (settings.json WaveformDragThresholdPixels).</summary>
    public double DragThreshold { get; set; } = Settings.DefaultWaveformDragThresholdPixels;

    /// <summary>Raised while dragging (the range so far) and on a plain click (null: cleared).</summary>
    public event Action<(int Start, int End)?>? SelectionEdited;

    private double? _pressX;
    private bool _dragging;

    public void Show(SoundEntry? entry)
    {
        bool sameSound = entry is not null && ReferenceEquals(entry, _entry);
        _entry = entry;
        _minsL = _maxsL = _minsR = _maxsR = null;
        if (sameSound) SetView(_samplesPerPixel, _viewStart); // an edit: keep zoom and position, clamped
        else SetView(null, 0);                                // another sound: fit it
    }

    /// <summary>Zooms keeping the frame under x where it is.</summary>
    private void ZoomAt(double factor, double x)
    {
        if (TotalFrames == 0) return;
        double anchor = _viewStart + x * SamplesPerPixel;
        double spp = ClampSpp(SamplesPerPixel / factor);
        SetView(spp, anchor - x * spp);
    }

    private double ClampSpp(double spp)
    {
        double fit = FitSpp;
        return Math.Clamp(spp, Math.Min(MinSpp, fit), fit);
    }

    private void SetView(double? spp, double start)
    {
        if (spp is { } z)
        {
            z = ClampSpp(z);
            spp = z >= FitSpp ? null : z; // zoomed all the way out = fit, which then follows resizes
        }
        _samplesPerPixel = spp;
        _viewStart = Math.Clamp(start, 0, Math.Max(0, TotalFrames - VisibleFrames));
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetView(_samplesPerPixel, _viewStart);
    }

    private int FrameAt(double x) =>
        (int)Math.Clamp(Math.Round(_viewStart + x * SamplesPerPixel), 0, TotalFrames);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_entry is null) return;
        double dx = e.Delta.X, dy = e.Delta.Y;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (shift || (dx != 0 && dy == 0)) // some platforms turn Shift+wheel into a horizontal delta
            SetView(_samplesPerPixel, _viewStart - (dx != 0 ? dx : dy) * ScrollStep * VisibleFrames);
        else if (dy != 0)
            ZoomAt(Math.Pow(ZoomStep, dy), e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_entry is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressX = e.GetPosition(this).X;
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressX is not { } pressX) return;
        double x = e.GetPosition(this).X;
        if (!_dragging && Math.Abs(x - pressX) < DragThreshold) return;
        _dragging = true;
        int a = FrameAt(pressX), b = FrameAt(x);
        (int Start, int End)? range = a == b ? null : (Math.Min(a, b), Math.Max(a, b));
        Selection = range;
        SelectionEdited?.Invoke(range);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressX is null) return;
        // Read before releasing capture: Capture(null) raises OnPointerCaptureLost
        // synchronously, which resets _dragging and made every drag look like a click.
        bool dragged = _dragging;
        _pressX = null;
        _dragging = false;
        e.Pointer.Capture(null);
        if (!dragged)
        {
            Selection = null;
            SelectionEdited?.Invoke(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressX = null;
        _dragging = false;
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        ctx.FillRectangle(Background, new Rect(bounds.Size));

        if (_entry is null || _entry.Data.Length == 0 || _entry.BitsPerSample == 0 || bounds.Width < 1) return;

        int w = Math.Max(1, (int)bounds.Width);
        bool stereo = _entry.Channels >= 2;
        double spp = SamplesPerPixel;
        var key = (bounds.Width, _viewStart, spp, stereo, (byte[]?)_entry.Data);
        if (_minsL is null || key != _envelopeKey)
        {
            (_minsL, _maxsL) = BuildEnvelope(_entry, w, channel: 0, _viewStart, spp);
            (_minsR, _maxsR) = stereo ? BuildEnvelope(_entry, w, channel: 1, _viewStart, spp) : (null, null);
            _envelopeKey = key;
        }

        if (_selection is { } sel && spp > 0)
        {
            double x1 = (sel.Start - _viewStart) / spp, x2 = (sel.End - _viewStart) / spp;
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)),
                new Rect(x1, 0, Math.Max(1, x2 - x1), bounds.Height));
        }

        double headX = _playhead is { } ph && spp > 0 ? (ph.Frame - _viewStart) / spp : double.NaN;

        var zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), 1);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x7a, 0xa8, 0xff)), 1);

        if (stereo && _minsR is not null && _maxsR is not null)
        {
            double half = bounds.Height / 2;
            DrawChannel(ctx, pen, zeroPen, _minsL, _maxsL!, bounds.Width, 0, half);
            DrawChannel(ctx, pen, zeroPen, _minsR, _maxsR, bounds.Width, half, half);
            var splitPen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xff, 0xff, 0xff)), 1);
            ctx.DrawLine(splitPen, new Point(0, half), new Point(bounds.Width, half));
        }
        else
        {
            DrawChannel(ctx, pen, zeroPen, _minsL, _maxsL!, bounds.Width, 0, bounds.Height);
        }

        if (!double.IsNaN(headX)) // on top of the waveform
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xd0, 0x40)), 1.5),
                new Point(headX, 0), new Point(headX, bounds.Height));
    }

    /// <summary>Draws one channel's envelope into the vertical band starting at top, height tall. NaN columns (past the end) are skipped.</summary>
    private static void DrawChannel(DrawingContext ctx, Pen pen, Pen zeroPen, float[] mins, float[] maxs, double width, double top, double height)
    {
        double midY = top + height / 2;
        ctx.DrawLine(zeroPen, new Point(0, midY), new Point(width, midY));

        for (int x = 0; x < mins.Length; x++)
        {
            if (float.IsNaN(mins[x])) continue;
            double y1 = midY - maxs[x] * (height / 2);
            double y2 = midY - mins[x] * (height / 2);
            if (y2 - y1 < 1) y2 = y1 + 1;
            ctx.DrawLine(pen, new Point(x + 0.5, y1), new Point(x + 0.5, y2));
        }
    }

    /// <summary>
    /// One min/max pair per pixel column over the visible frames (start, spp frames per
    /// column), reading the given channel. Zoomed past one sample per pixel, neighboring
    /// columns share a sample, which draws as steps.
    /// </summary>
    private static (float[] Mins, float[] Maxs) BuildEnvelope(SoundEntry entry, int columns, int channel, double start, double spp)
    {
        var mins = new float[columns];
        var maxs = new float[columns];
        Array.Fill(mins, float.NaN);

        int bytesPerSample = Math.Max(entry.BitsPerSample, 8) / 8;
        int channelCount = Math.Max(entry.Channels, 1);
        int frameBytes = bytesPerSample * channelCount;
        int totalFrames = SndEdit.FrameCount(entry);
        if (frameBytes <= 0 || totalFrames <= 0 || spp <= 0) return (mins, maxs);

        int channelOffset = entry.DataOffset + Math.Min(channel, channelCount - 1) * bytesPerSample;
        for (int col = 0; col < columns; col++)
        {
            double f0 = start + col * spp;
            long startFrame = (long)Math.Floor(f0);
            if (startFrame >= totalFrames) break;
            long endFrame = Math.Min(totalFrames, Math.Max(startFrame + 1, (long)Math.Ceiling(f0 + spp)));
            float mn = 1, mx = -1;
            for (long f = startFrame; f < endFrame; f++)
            {
                float v = ReadSample(entry.Data, channelOffset + (int)f * frameBytes, entry.BitsPerSample, entry.FormatTag);
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            if (mn > mx) { mn = 0; mx = 0; }
            mins[col] = mn;
            maxs[col] = mx;
        }
        return (mins, maxs);
    }

    /// <summary>Reads one sample (at the already channel-offset position) scaled to -1..1.</summary>
    private static float ReadSample(byte[] d, int pos, int bits, ushort formatTag)
    {
        if (pos < 0 || pos + Math.Max(bits, 8) / 8 > d.Length) return 0;
        if (bits == 32 && formatTag == 3)
            return BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos, 4)));
        return bits switch
        {
            8 => d[pos] / 255f * 2f - 1f, // WAV 8-bit PCM is unsigned
            16 => (short)(d[pos] | (d[pos + 1] << 8)) / 32768f,
            24 => Sign24(d[pos] | (d[pos + 1] << 8) | (d[pos + 2] << 16)) / 8388608f,
            32 => BitConverter.ToInt32(d, pos) / 2147483648f,
            _ => 0,
        };
    }

    private static int Sign24(int v) => (v & 0x800000) != 0 ? v | unchecked((int)0xFF000000) : v;
}
