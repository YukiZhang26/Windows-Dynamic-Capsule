using DynamicCapsule.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfSystemColors = System.Windows.SystemColors;

namespace DynamicCapsule.Services;

internal sealed class AccessibilityPreferencesService : IDisposable
{
    private bool _disposed;

    internal AccessibilityPreferencesService()
    {
        Current = ReadCurrent();
        SystemParameters.StaticPropertyChanged +=
            OnSystemParametersChanged;
    }

    internal AccessibilityPreferences Current { get; private set; }

    internal event Action<AccessibilityPreferences>? PreferencesChanged;

    internal void ApplyTheme(ResourceDictionary resources)
    {
        var highContrast = Current.HighContrast;
        resources["CompactCapsuleSurfaceBrush"] = highContrast
            ? WpfSystemColors.WindowBrush
            : CreateBrush(22, 24, 29, 200);
        resources["CapsuleSurfaceBrush"] = highContrast
            ? WpfSystemColors.WindowBrush
            : CreateBrush(22, 24, 29, 242);
        resources["WindowSurfaceBrush"] = highContrast
            ? WpfSystemColors.WindowBrush
            : CreateBrush(20, 21, 26);
        resources["PrimaryTextBrush"] = highContrast
            ? WpfSystemColors.WindowTextBrush
            : CreateBrush(243, 244, 247);
        resources["SecondaryTextBrush"] = highContrast
            ? WpfSystemColors.WindowTextBrush
            : CreateBrush(173, 179, 192);
        resources["MutedTextBrush"] = highContrast
            ? WpfSystemColors.GrayTextBrush
            : CreateBrush(119, 127, 142);
        resources["AccentBrush"] = highContrast
            ? WpfSystemColors.HighlightBrush
            : CreateBrush(42, 99, 245);
        resources["AccentTextBrush"] = highContrast
            ? WpfSystemColors.HighlightTextBrush
            : WpfBrushes.White;
        resources["AccentLabelBrush"] = highContrast
            ? WpfSystemColors.HotTrackBrush
            : CreateBrush(142, 169, 238);
        resources["BorderBrush"] = highContrast
            ? WpfSystemColors.ActiveBorderBrush
            : CreateBrush(255, 255, 255, 56);
        resources["SubtleSurfaceBrush"] = highContrast
            ? WpfSystemColors.ControlBrush
            : CreateBrush(255, 255, 255, 20);
        resources["DangerBrush"] = highContrast
            ? WpfSystemColors.WindowTextBrush
            : CreateBrush(255, 119, 119);
        resources["ProgressTrackBrush"] = highContrast
            ? WpfSystemColors.ControlBrush
            : CreateBrush(255, 255, 255, 38);
        resources["ProgressBrush"] = highContrast
            ? WpfSystemColors.HighlightBrush
            : CreateBrush(88, 214, 141);
        resources["FocusBrush"] = highContrast
            ? WpfSystemColors.HotTrackBrush
            : CreateBrush(126, 162, 255);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemParameters.StaticPropertyChanged -=
            OnSystemParametersChanged;
    }

    private void OnSystemParametersChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
                nameof(SystemParameters.HighContrast)
                or nameof(SystemParameters.ClientAreaAnimation)))
        {
            return;
        }

        var next = ReadCurrent();
        if (next == Current)
        {
            return;
        }

        Current = next;
        PreferencesChanged?.Invoke(next);
    }

    private static AccessibilityPreferences ReadCurrent()
    {
        return new AccessibilityPreferences(
            SystemParameters.HighContrast,
            !SystemParameters.ClientAreaAnimation);
    }

    private static SolidColorBrush CreateBrush(
        byte red,
        byte green,
        byte blue,
        byte alpha = byte.MaxValue)
    {
        var brush = new SolidColorBrush(
            WpfColor.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
