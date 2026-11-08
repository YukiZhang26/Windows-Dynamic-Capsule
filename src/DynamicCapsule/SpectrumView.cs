using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace DynamicCapsule;

public sealed class SpectrumView : FrameworkElement
{
    public int BarCount { get; set; } = 5;
    public bool GradientStyle { get; set; }
    private Brush? _heightGradient;
    private double _gradientHeight = -1;
    private static readonly Brush SpectrumBrush = CreateSpectrumBrush();
    private static Brush CreateSpectrumBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 1),
            EndPoint = new System.Windows.Point(0, 0)
        };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(230, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(195, 255, 255, 255), 0.25));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(65, 255, 255, 255), 0.65));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }
    private readonly double[] _heights = new double[96];
    private readonly double[] _targets = new double[96];
    private readonly double[] _velocities = new double[96];
    private long _lastTick;
    private bool _listening;
    private bool _reducedMotion;

    public SpectrumView()
    {
        Loaded += (_, _) => RefreshRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => RefreshRendering();
    }

    private void RefreshRendering()
    {
        if (!IsLoaded || !IsVisible || _reducedMotion) { StopRendering(); return; }
        if (_listening) return;
        _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _listening = true;
    }

    private void StopRendering()
    {
        if (_listening) CompositionTarget.Rendering -= OnRendering;
        _listening = false;
        _lastTick = 0;
        Array.Clear(_velocities);
    }

    internal static void Smooth(ref double value, ref double velocity, double target, double seconds)
    {
        // Critically damped response: continuous position and velocity, no decorative bounce.
        var omega = target > value ? 28.0 : 16.0;
        var delta = value - target;
        var c = velocity + omega * delta;
        var decay = Math.Exp(-omega * seconds);
        value = target + (delta + c * seconds) * decay;
        velocity = (velocity - omega * c * seconds) * decay;
        if (value < 0 || value > 1) { value = Math.Clamp(value, 0, 1); velocity = 0; }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Clamp((now - _lastTick) / (double)Stopwatch.Frequency, 0, 0.05);
        _lastTick = now;
        var changed = false;
        for (var i = 0; i < BarCount; i++)
        {
            if (Math.Abs(_heights[i] - _targets[i]) < 0.0005 && Math.Abs(_velocities[i]) < 0.005)
            {
                changed |= _heights[i] != _targets[i];
                _heights[i] = _targets[i]; _velocities[i] = 0;
                continue;
            }
            Smooth(ref _heights[i], ref _velocities[i], _targets[i], seconds);
            changed = true;
        }
        if (changed) InvalidateVisual();
    }
    internal void Update(float[] bands, bool reducedMotion)
    {
        _reducedMotion = reducedMotion;
        for (var i = 0; i < BarCount; i++)
        {
            double target = 0;
            var from = i * bands.Length / BarCount;
            var to = (i + 1) * bands.Length / BarCount;
            for (var bin = from; bin < to; bin++) target = Math.Max(target, bands[bin]);
            if (BarCount > bands.Length && bands.Length > 0)
            {
                // Interpolate measured bands for a denser view, without inventing audio energy.
                var position = i * (bands.Length - 1.0) / (BarCount - 1);
                var lower = (int)position;
                var fraction = position - lower;
                target = bands[lower] * (1 - fraction) + bands[Math.Min(lower + 1, bands.Length - 1)] * fraction;
            }
            // Reduced-motion mode keeps a static indicator, never simulated audio.
            // Expand contrast in the measured levels; no per-frame auto normalization.
            if (GradientStyle) target = Math.Pow(Math.Clamp((target - 0.12) / 0.73, 0, 1), 1.6);
            if (reducedMotion) target = 0;
            _targets[i] = target;
            if (reducedMotion) { _heights[i] = 0; _velocities[i] = 0; }
        }
        RefreshRendering();
        if (reducedMotion) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var brush = TryFindResource("PrimaryTextBrush") as Brush ?? Brushes.White;
        if (GradientStyle && !SystemParameters.HighContrast)
        {
            if (_heightGradient is null || _gradientHeight != ActualHeight)
            {
                var gradient = ((LinearGradientBrush)SpectrumBrush).Clone();
                gradient.MappingMode = BrushMappingMode.Absolute;
                gradient.StartPoint = new System.Windows.Point(0, ActualHeight);
                gradient.EndPoint = new System.Windows.Point(0, 0);
                gradient.Freeze();
                _heightGradient = gradient;
                _gradientHeight = ActualHeight;
            }
            brush = _heightGradient;
        }
        var step = ActualWidth / BarCount;
        var width = Math.Min(GradientStyle ? 2 : 3, step * 0.55);
        for (var i = 0; i < BarCount; i++)
        {
            var minimumHeight = GradientStyle ? 0.75 : 2;
            var height = minimumHeight + _heights[i] * Math.Max(0, ActualHeight - minimumHeight);
            dc.DrawRoundedRectangle(brush, null,
                new Rect(i * step + (step - width) / 2, GradientStyle ? ActualHeight - height : (ActualHeight - height) / 2, width, height), width / 2, width / 2);
        }
    }
}
