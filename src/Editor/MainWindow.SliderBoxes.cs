using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Editor;

public partial class MainWindow
{
    // Palette Color and Adjustments sliders each pair with a number box.
    private void WireSliderBoxes()
    {
        PairSliderBox(RSlider, RBox); PairSliderBox(GSlider, GBox);
        PairSliderBox(BSlider, BBox); PairSliderBox(ASlider, ABox);
        PairSliderBox(RAddSlider, RAddBox); PairSliderBox(GAddSlider, GAddBox);
        PairSliderBox(BAddSlider, BAddBox); PairSliderBox(HueSlider, HueBox);
        PairSliderBox(SaturationSlider, SaturationBox); PairSliderBox(LightenSlider, LightenBox);
    }

    /// <summary>Fills each palette channel slider's track with its theme channel color.</summary>
    private void ApplySliderChannelColors(ThemeDef theme)
    {
        var red = new SolidColorBrush(Color.Parse(theme.SliderRed));
        var green = new SolidColorBrush(Color.Parse(theme.SliderGreen));
        var blue = new SolidColorBrush(Color.Parse(theme.SliderBlue));
        var neutral = new SolidColorBrush(Color.Parse(theme.SliderNeutral));
        foreach (var (slider, brush) in new[] { (RSlider, red), (GSlider, green), (BSlider, blue), (ASlider, neutral),
                     (RAddSlider, red), (GAddSlider, green), (BAddSlider, blue),
                     (HueSlider, neutral), (SaturationSlider, neutral), (LightenSlider, neutral) })
        {
            // Local keys shadow the app-wide Accent fill; disabled keeps the theme's Dim.
            slider.Resources["SliderTrackValueFill"] = brush;
            slider.Resources["SliderTrackValueFillPointerOver"] = brush;
            slider.Resources["SliderTrackValueFillPressed"] = brush;
        }
    }

    /// <summary>Box mirrors the slider; typed values apply on Enter or focus loss, spins at once.</summary>
    private static void PairSliderBox(Slider slider, NumericUpDown box)
    {
        box.Minimum = (decimal)slider.Minimum;
        box.Maximum = (decimal)slider.Maximum;
        box.Value = (decimal)Math.Round(slider.Value);
        slider.ValueChanged += (_, e) => box.Value = (decimal)Math.Round(e.NewValue);

        // Posted so the box has parsed its text or finished its spin first.
        void Apply() => Dispatcher.UIThread.Post(() =>
        {
            var shown = (decimal)Math.Round(slider.Value);
            if (box.Value is not { } v) box.Value = shown;
            else if (v != shown) slider.Value = (double)v; // unchanged keeps a fractional drag value
        });
        box.Spinned += (_, _) => Apply();
        box.LostFocus += (_, _) => Apply();
        box.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) Apply(); },
            RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
