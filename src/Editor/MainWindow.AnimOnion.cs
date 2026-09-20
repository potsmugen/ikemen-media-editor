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
    /// <summary>Read at startup, before AnimCanvas exists; applied once it does.</summary>
    private double _animOnionOpacity = Settings.DefaultOnionOpacity;
    private int? _animOnionAction;
    /// <summary>1-based, as shown.</summary>
    private int? _animOnionFrame;

    /// <summary>Pinned frame when both fields are set, else the previous frame (none on frame 1).</summary>
    private Air.AnimationElement? AnimOnionSource()
    {
        if (_animOnionAction is { } a && _animOnionFrame is { } f)
            return _air?.Find(a) is { } pinned && f >= 1 && f <= pinned.Elements.Count ? pinned.Elements[f - 1] : null;
        int i = FrameList.SelectedIndex;
        return _action is not null && i > 0 && i < _action.Elements.Count ? _action.Elements[i - 1] : null;
    }

    /// <summary>Called from ShowFrame.</summary>
    private void ShowAnimOnion()
    {
        if (AnimCanvas is null) return;
        var src = _animOnion ? AnimOnionSource() : null;
        var sprite = src is null ? null : _file?.Find(src.Group, src.Index);
        AnimCanvas.ShowOnion(sprite, sprite?.Palette, src is null ? default : new Point(src.OffsetX, src.OffsetY), _animOnionGrayscale);
        AnimCanvas.GhostBoxes = src is null || !_animOnionClsn ? new List<BoxOverlay>() : BoxesFor(src);
    }

    private void SyncAnimOnionControls()
    {
        if (AnimOnionEnabledBox is null) return;
        AnimOnionEnabledBox.IsChecked = _animOnion;
        AnimOnionGrayscaleBox.IsChecked = _animOnionGrayscale;
        AnimOnionClsnBox.IsChecked = _animOnionClsn;
        AnimOnionOpacitySlider.Value = Math.Round((AnimCanvas.OnionOpacityOverride ?? SpriteView.OnionOpacity) * 100);
        AnimOnionActionBox.Value = _animOnionAction;
        AnimOnionFrameBox.Value = _animOnionFrame;
    }

    private void OnToggleAnimOnion(object? sender, RoutedEventArgs e)
    {
        _animOnion = !_animOnion;
        _settings.AnimOnionSkin = _animOnion;
        _settings.Save();
        ApplyOnionOptionsEnabled();
        AnimCanvas.ShowOnionSkin = _animOnion;
        SyncViewMenu();
        ShowFrame();
    }

    private void OnToggleAnimOnionGrayscale(object? sender, RoutedEventArgs e)
    {
        _animOnionGrayscale = !_animOnionGrayscale;
        _settings.AnimOnionGrayscale = _animOnionGrayscale;
        _settings.Save();
        SyncViewMenu();
        ShowFrame();
    }

    private void OnToggleAnimOnionClsn(object? sender, RoutedEventArgs e)
    {
        _animOnionClsn = !_animOnionClsn;
        _settings.AnimOnionClsn = _animOnionClsn;
        _settings.Save();
        SyncViewMenu();
        ShowFrame();
    }

    private void OnAnimOnionOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || AnimCanvas is null) return;
        AnimCanvas.OnionOpacityOverride = AnimOnionOpacitySlider.Value / 100;
        _settings.AnimOnionOpacity = AnimCanvas.OnionOpacityOverride.Value;
        _settings.Save();
        AnimCanvas.InvalidateVisual();
    }

    private void OnAnimOnionFieldChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _animOnionAction = AnimOnionActionBox.Value is { } a ? (int)a : null;
        _animOnionFrame = AnimOnionFrameBox.Value is { } f ? (int)f : null;
        _settings.AnimOnionAction = _animOnionAction;
        _settings.AnimOnionFrame = _animOnionFrame;
        _settings.Save();
        ShowFrame();
    }

    private void OnAnimOnionUseAuto(object? sender, RoutedEventArgs e)
    {
        _animOnionAction = _animOnionFrame = null;
        _settings.AnimOnionAction = _settings.AnimOnionFrame = null;
        _settings.Save();
        SyncViewMenu();
        ShowFrame();
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
        _settings.AnimOnionAction = _animOnionAction;
        _settings.AnimOnionFrame = _animOnionFrame;
        _settings.Save();
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
        if (SetFrameClsn(src.Hurt.Count > 0 ? src.Hurt : null, src.Attack.Count > 0 ? src.Attack : null, "copy Clsn from onion skin"))
            StatusText.Text = $"Copied Clsn from the onion skin frame into frame {FrameList.SelectedIndex + 1}.";
    }
}
