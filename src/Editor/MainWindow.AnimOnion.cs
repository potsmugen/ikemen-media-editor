using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Editor;

/// <summary>
/// Animation Onion Skin (Animations tab only): ghosts the previous frame, or a
/// pinned action + frame, as its sprite (with its offset) plus its Clsn drawn
/// dashed, exactly where that frame draws them. The Sprite Onion Skin panel
/// stays on the Sprites tab.
/// </summary>
public partial class MainWindow
{
    private bool _animOnion;
    private bool _animOnionGrayscale = true;
    private bool _animOnionClsn = true;
    private bool _animOnionOnTop;
    /// <summary>Read at startup, before AnimCanvas exists; applied once it does.</summary>
    private double _animOnionOpacity = Settings.DefaultOnionOpacity;

    /// <summary>Pinned frame when both fields are set, else the previous frame (none on frame 1).</summary>
    private Air.AnimationElement? AnimOnionSource() => AnimOnionLocation()?.Element;

    /// <summary>The frame the onion shows, with the action it lives in (the pin can point at another action).</summary>
    private (Air.AnimationAction Action, Air.AnimationElement Element)? AnimOnionLocation()
    {
        if (_animOnionAction is { } a && _animOnionFrame is { } f)
            return _air?.Find(a) is { } pinned && f >= 1 && f <= pinned.Elements.Count ? (pinned, pinned.Elements[f - 1]) : null;
        int i = FrameList.SelectedIndex;
        return _action is not null && i > 0 && i < _action.Elements.Count ? (_action, _action.Elements[i - 1]) : null;
    }

    /// <summary>Called from ShowFrame.</summary>
    private void ShowAnimOnion()
    {
        if (AnimCanvas is null) return;
        var src = _animOnion ? AnimOnionSource() : null;
        SyncAnimOnionSwapButton(src);
        var sprite = src is null ? null : FindAnimSprite(src.Group, src.Index);
        AnimCanvas.ShowOnion(sprite, sprite?.Palette, src is null ? default : new Point(src.OffsetX, src.OffsetY), _animOnionGrayscale,
            reuse: _clock is not null, // nothing edits pixels during playback
            flipH: src?.FlipH ?? false, flipV: src?.FlipV ?? false, scaleX: src?.ScaleX ?? 1, scaleY: src?.ScaleY ?? 1, angle: src?.Angle ?? 0);
        AnimCanvas.GhostBoxes = src is null || !_animOnionClsn ? new List<BoxOverlay>() : BoxesFor(src);
    }

    /// <summary>Dims Swap unless an onion frame is shown and it isn't the frame shown now.</summary>
    private void SyncAnimOnionSwapButton(Air.AnimationElement? src)
    {
        if (AnimOnionSwapButton is null) return;
        if (_clock is not null) return; // playback would flip it every pass over the pinned frame
        bool same = src is not null && ReferenceEquals(src, CurrentElement);
        AnimOnionSwapButton.IsEnabled = src is not null && !same;
        ToolTip.SetTip(AnimOnionSwapButton, same ? "The onion skin is the active frame." : "Swaps the onion skin with the active frame.");
    }

    private void SyncAnimOnionControls()
    {
        if (AnimOnionEnabledBox is null) return;
        AnimOnionEnabledBox.IsChecked = _animOnion;
        AnimOnionGrayscaleBox.IsChecked = _animOnionGrayscale;
        AnimOnionClsnBox.IsChecked = _animOnionClsn;
        AnimOnionOnTopBox.IsChecked = _animOnionOnTop;
        AnimOnionAutoBox.IsChecked = _animOnionAction is null || _animOnionFrame is null;
        AnimOnionOpacitySlider.Value = Math.Round((AnimCanvas.OnionOpacityOverride ?? SpriteView.OnionOpacity) * 100);
        AnimOnionActionBox.Value = _animOnionAction;
        AnimOnionFrameBox.Value = _animOnionFrame;
    }

    private void OnToggleAnimOnion(object? sender, RoutedEventArgs e)
    {
        _animOnion = !_animOnion;
        _settings.Animations.AnimOnionSkin = _animOnion;
        _settings.Save();
        ApplyOnionOptionsEnabled();
        AnimCanvas.ShowOnionSkin = _animOnion;
        SyncViewMenu();
        ShowFrame();
    }

    private void OnToggleAnimOnionGrayscale(object? sender, RoutedEventArgs e)
    {
        _animOnionGrayscale = !_animOnionGrayscale;
        _settings.Animations.AnimOnionGrayscale = _animOnionGrayscale;
        _settings.Save();
        SyncViewMenu();
        ShowFrame();
    }

    private void OnToggleAnimOnionClsn(object? sender, RoutedEventArgs e)
    {
        _animOnionClsn = !_animOnionClsn;
        _settings.Animations.AnimOnionClsn = _animOnionClsn;
        _settings.Save();
        SyncViewMenu();
        ShowFrame();
    }

    private void OnToggleAnimOnionOnTop(object? sender, RoutedEventArgs e)
    {
        _animOnionOnTop = !_animOnionOnTop;
        AnimCanvas.OnionOnTop = _animOnionOnTop;
        AnimCanvas.InvalidateVisual();
    }

    private void OnAnimOnionOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || AnimCanvas is null) return;
        AnimCanvas.OnionOpacityOverride = AnimOnionOpacitySlider.Value / 100;
        _settings.Animations.AnimOnionOpacity = AnimCanvas.OnionOpacityOverride.Value;
        _settings.Save();
        AnimCanvas.InvalidateVisual();
    }

    private void OnAnimOnionFieldChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _animOnionAction = AnimOnionActionBox.Value is { } a ? (int)a : null;
        _animOnionFrame = AnimOnionFrameBox.Value is { } f ? (int)f : null;
        AnimOnionAutoBox.IsChecked = _animOnionAction is null || _animOnionFrame is null;
        ShowFrame();
    }

    /// <summary>Auto toggle: on follows the previous frame, off pins the active one.</summary>
    private void OnToggleAnimOnionAuto(object? sender, RoutedEventArgs e)
    {
        if (_animOnionAction is not null && _animOnionFrame is not null)
        {
            _animOnionAction = _animOnionFrame = null;
            SyncViewMenu();
            ShowFrame();
        }
        else PinFrameAsOnion();
        SyncViewMenu(); // the toggle shows the pin, not the click
    }

    /// <summary>
    /// Swaps what the canvas shows with the onion skin: the onion is pinned to the
    /// frame shown now, and the old onion frame is shown, in its action.
    /// </summary>
    private void OnSwapAnimOnion(object? sender, RoutedEventArgs e)
    {
        if (_action is not { } action || CurrentElement is not { } current) return;
        if (AnimOnionLocation() is not { } onion || ReferenceEquals(onion.Element, current)) return;
        int frame = FrameList.SelectedIndex + 1;
        _animOnionAction = action.Number;
        _animOnionFrame = frame;
        JumpToFrame(onion.Action, onion.Element);
        SyncViewMenu();
        ShowFrame(); // JumpToFrame can bail before the frame moves; the ghost still has to follow the pin
        StatusText.Text = $"Onion skin swapped with action {onion.Action.Number}, frame {onion.Action.Elements.IndexOf(onion.Element) + 1}.";
    }

    private void OnToggleAnimOnionPanel(object? sender, RoutedEventArgs e)
    {
        AnimOnionPanelBody.IsVisible = !AnimOnionPanelBody.IsVisible;
        AnimOnionPanelToggle.Content = AnimOnionPanelBody.IsVisible ? "\u25B2" : "\u25BC";
    }

    /// <summary>Frame table menu: pins the selected frame and turns the onion skin on.</summary>
    private void PinFrameAsOnion()
    {
        if (_action is null || CurrentElement is null) return;
        _animOnionAction = _action.Number;
        _animOnionFrame = FrameList.SelectedIndex + 1;
        if (!_animOnion) OnToggleAnimOnion(null, new RoutedEventArgs()); // also syncs and redraws
        else { SyncViewMenu(); ShowFrame(); }
        StatusText.Text = $"Onion skin pinned to action {_animOnionAction}, frame {_animOnionFrame}.";
    }

    /// <summary>Copies the onion skin frame's Clsn (what the ghosts show) into this frame; works with the onion off too.</summary>
    private void OnCopyClsnFromOnion(object? sender, RoutedEventArgs e)
    {
        if (CurrentElement is null) return;
        if (AnimOnionSource() is not { } src)
        {
            StatusText.Text = _animOnionAction is not null && _animOnionFrame is not null
                ? $"Action {_animOnionAction} has no frame {_animOnionFrame}."
                : "The first frame has no previous frame to copy from.";
            return;
        }
        if (src.Hurt.Count == 0 && src.Attack.Count == 0)
        {
            StatusText.Text = "The onion skin frame has no Clsn.";
            return;
        }
        // Copied first: the onion frame may be one of the selected frames.
        List<Air.ClsnBox>? hurt = src.Hurt.Count > 0 ? [.. src.Hurt] : null, attack = src.Attack.Count > 0 ? [.. src.Attack] : null;
        if (SelectedFrameTargets() is { Count: > 1 } targets)
        {
            if (EditFramesClsn("copy Clsn from onion skin", targets, (a, i) => ApplyFrameClsn(a, i, hurt, attack)))
                StatusText.Text = $"Copied Clsn from the onion skin frame into {targets.Count} frames.";
            return;
        }
        if (SetFrameClsn(hurt, attack, "copy Clsn from onion skin"))
            StatusText.Text = $"Copied Clsn from the onion skin frame into frame {FrameList.SelectedIndex + 1}.";
    }
}
