using DynamicCapsule.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace DynamicCapsule;

public partial class TimerWindow : Window
{
    internal TimerWindow(AccessibilityPreferences accessibilityPreferences)
    {
        InitializeComponent();
        AutomationProperties.SetHelpText(
            this,
            $"{accessibilityPreferences.Summary}。Enter 开始，Esc 取消。");
        MinutesTextBox.SelectAll();
        MinutesTextBox.Focus();
    }

    internal TimeSpan Duration { get; private set; }
    internal string? TimerTitle { get; private set; }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(
                MinutesTextBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var minutes)
            || minutes is < 1 or > 1440)
        {
            ValidationText.Text = "请输入 1 到 1440 之间的整数分钟。";
            ValidationText.Visibility = Visibility.Visible;
            MinutesTextBox.Focus();
            MinutesTextBox.SelectAll();
            return;
        }

        Duration = TimeSpan.FromMinutes(minutes);
        TimerTitle = string.IsNullOrWhiteSpace(TitleTextBox.Text)
            ? null
            : TitleTextBox.Text.Trim();
        DialogResult = true;
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnStartClick(this, new RoutedEventArgs());
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
