namespace Editor;

/// <summary>
/// Turns fractional wheel or pinch input into whole zoom steps. A mouse notch is 1;
/// touchpads send many small deltas that add up, and reversing drops the leftover.
/// </summary>
public sealed class ZoomSteps
{
    private double _sum;

    public int Add(double delta)
    {
        if (!double.IsFinite(delta) || delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_sum)) _sum = 0;
        _sum += delta;
        int whole = (int)Math.Truncate(_sum);
        _sum -= whole;
        return whole;
    }
}
