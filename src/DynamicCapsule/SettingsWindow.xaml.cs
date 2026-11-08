using DynamicCapsule.Models;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace DynamicCapsule;

public partial class SettingsWindow : Window
{
    private readonly bool _topStashed;
    private bool _reduceMotion;
    private Button? _pressedButton;
    private bool AnimateControls => !_reduceMotion && EnableAnimationsCheckBox.IsChecked == true;
    private CheckBox[] MotionSwitches => [EnableAnimationsCheckBox, HideInFullscreenCheckBox,
        StartWithWindowsCheckBox, DoNotDisturbCheckBox, AiMonitorCheckBox];

    internal SettingsWindow(
        AppSettings settings,
        string settingsPath,
        AccessibilityPreferences accessibilityPreferences,
        NotificationListenerStatus notificationStatus,
        string notificationStatusMessage)
    {
        InitializeComponent();
        _topStashed = settings.TopStashed;

        MonitorTargetCombo.SelectedIndex =
            settings.MonitorTarget == MonitorTargetMode.PrimaryDisplay
                ? 1
                : 0;
        TopGapSlider.Value = settings.TopGap;
        TransparencySlider.Value = settings.BackgroundTransparency;
        TransparencyValueText.Text = $"{TransparencySlider.Value:0}%";
        TransparencySlider.ValueChanged += (_, _) =>
            TransparencyValueText.Text = $"{TransparencySlider.Value:0}%";
        EnableAnimationsCheckBox.IsChecked = settings.EnableAnimations;
        HideInFullscreenCheckBox.IsChecked = settings.HideInFullscreen;
        DoNotDisturbCheckBox.IsChecked = settings.DoNotDisturb;
        AiMonitorCheckBox.IsChecked = settings.MonitorAiActivity;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        LyricsFallbackProviderCombo.SelectedIndex =
            settings.LyricsFallbackProvider == LyricsFallbackProvider.QqMusic
                ? 1
                : 0;
        PrivacyLevelCombo.SelectedIndex = (int)settings.PrivacyLevel;
        NotificationAllowListTextBox.Text =
            string.Join(Environment.NewLine, settings.NotificationAllowList);
        NotificationBlockListTextBox.Text =
            string.Join(Environment.NewLine, settings.NotificationBlockList);
        SettingsPathText.Text = settingsPath;

        TopGapSlider.ValueChanged += OnTopGapValueChanged;
        UpdateTopGapText();
        ApplyAccessibilityPreferences(accessibilityPreferences);
        UpdateNotificationStatus(
            notificationStatus,
            notificationStatusMessage);
        foreach (var toggle in MotionSwitches)
        {
            toggle.Loaded += (_, _) => UpdateSwitch(toggle, false);
            toggle.Checked += OnSwitchChanged;
            toggle.Unchecked += OnSwitchChanged;
        }
        SettingsTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, SettingsTabs) && IsLoaded) AnimatePage();
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            var node = e.OriginalSource as DependencyObject;
            while (node is Visual && node is not Button) node = VisualTreeHelper.GetParent(node);
            if (node is Button button)
            {
                _pressedButton = button;
                AnimateButton(button, 0.96);
            }
        };
        PreviewMouseLeftButtonUp += (_, _) => ReleaseButton();
        LostMouseCapture += (_, _) => ReleaseButton();
    }

    internal AppSettings? SavedSettings { get; private set; }
    internal event Func<EventPrivacyLevel, bool>?
        NotificationPreviewRequested;

    internal void UpdateDoNotDisturb(bool isEnabled)
    {
        DoNotDisturbCheckBox.IsChecked = isEnabled;
    }

    internal void UpdateNotificationStatus(
        NotificationListenerStatus status,
        string message)
    {
        NotificationStatusTitleText.Text = status switch
        {
            NotificationListenerStatus.Allowed =>
                "已连接 Windows 通知",
            NotificationListenerStatus.PackageIdentityRequired =>
                "开发版未连接 Windows 通知",
            NotificationListenerStatus.Denied =>
                "Windows 通知权限已拒绝",
            NotificationListenerStatus.Unsupported =>
                "当前系统不支持通知监听",
            NotificationListenerStatus.Unavailable =>
                "Windows 通知服务不可用",
            _ => "等待 Windows 通知授权"
        };

        NotificationStatusMessageText.Text = status switch
        {
            NotificationListenerStatus.Allowed =>
                "已获得系统授权；进入 Windows 通知中心的新消息可显示为顶部临时弹窗。",
            NotificationListenerStatus.PackageIdentityRequired =>
                "当前是未打包开发版，可使用“预览通知”调整效果；"
                + "真实微信通知需要安装 Microsoft Store/MSIX 版。",
            NotificationListenerStatus.Denied =>
                "请打开 Windows 通知隐私设置并允许访问，然后重新启动应用。",
            NotificationListenerStatus.Unspecified =>
                "系统尚未返回通知读取授权结果。",
            _ => message
        };

        var brushKey = status switch
        {
            NotificationListenerStatus.Allowed => "ProgressBrush",
            NotificationListenerStatus.PackageIdentityRequired
                or NotificationListenerStatus.Unspecified =>
                "AccentLabelBrush",
            _ => "DangerBrush"
        };
        NotificationStatusDot.SetResourceReference(
            Shape.FillProperty,
            brushKey);
    }

    internal void ApplyAccessibilityPreferences(
        AccessibilityPreferences preferences)
    {
        _reduceMotion = preferences.ReduceMotion || preferences.HighContrast;
        if (_reduceMotion) ResetMotion();
        SystemAccessibilityText.Text = preferences switch
        {
            { HighContrast: true, ReduceMotion: true } =>
                "系统高对比度和减少动画均已开启；应用已自动适配。",
            { HighContrast: true } =>
                "系统高对比度已开启；应用颜色已自动适配。",
            { ReduceMotion: true } =>
                "系统减少动画已开启；胶囊动画将被临时关闭。",
            _ => "键盘：Tab 导航，Ctrl+S 保存，Esc 取消。"
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MonitorTargetCombo.Focus();
        AnimatePage();
    }

    private void OnSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (!AnimateControls) ResetMotion();
        UpdateSwitch((CheckBox)sender, AnimateControls);
    }

    private void UpdateSwitch(CheckBox toggle, bool animate)
    {
        toggle.ApplyTemplate();
        if (toggle.Template.FindName("Knob", toggle) is not FrameworkElement knob
            || knob.RenderTransform is not TranslateTransform translation) return;
        if (translation.IsFrozen) knob.RenderTransform = translation = translation.Clone();
        AnimateValue(translation, TranslateTransform.XProperty,
            toggle.IsChecked == true ? 18 : 0, animate, 0.22);
    }

    private static void AnimateValue(Animatable target, DependencyProperty property,
        double destination, bool animate, double seconds)
    {
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, destination);
        if (animate)
            target.BeginAnimation(property, new DoubleAnimation(from, destination, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            }, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimatePage()
    {
        if (SettingsTabs.Template.FindName("PageSurface", SettingsTabs) is not FrameworkElement page) return;
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        var translation = (TranslateTransform)page.RenderTransform;
        if (translation.IsFrozen) page.RenderTransform = translation = translation.Clone();
        translation.BeginAnimation(TranslateTransform.YProperty, null);
        translation.Y = AnimateControls ? 8 : 0;
        AnimateValue(translation, TranslateTransform.YProperty, 0, AnimateControls, 0.26);
        if (AnimateControls)
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(0.2))
            { FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateButton(Button button, double scale)
    {
        if (button.Template.FindName("Surface", button) is not FrameworkElement surface
            || surface.RenderTransform is not ScaleTransform transform) return;
        if (transform.IsFrozen) surface.RenderTransform = transform = transform.Clone();
        AnimateValue(transform, ScaleTransform.ScaleXProperty, scale, AnimateControls, 0.14);
        AnimateValue(transform, ScaleTransform.ScaleYProperty, scale, AnimateControls, 0.14);
    }

    private void ReleaseButton()
    {
        if (_pressedButton is not { } button) return;
        _pressedButton = null;
        AnimateButton(button, 1);
    }

    private void ResetMotion()
    {
        foreach (var toggle in MotionSwitches) UpdateSwitch(toggle, false);
        if (SettingsTabs.Template?.FindName("PageSurface", SettingsTabs) is FrameworkElement page)
        {
            page.BeginAnimation(OpacityProperty, null);
            page.Opacity = 1;
            if (page.RenderTransform is TranslateTransform translation)
            {
                if (translation.IsFrozen) page.RenderTransform = translation = translation.Clone();
                AnimateValue(translation, TranslateTransform.YProperty, 0, false, 0);
            }
        }
        ReleaseButton();
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.S
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnSaveClick(this, new RoutedEventArgs());
        }
    }

    private void OnTopGapValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTopGapText();
    }

    private void OnOpenNotificationSettingsClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:privacy-notifications",
                UseShellExecute = true
            });
            NotificationActionStatusText.Text =
                "已打开 Windows 通知隐私设置。";
        }
        catch (Exception exception)
        {
            NotificationActionStatusText.Text =
                $"无法打开系统设置：{exception.Message}";
        }
    }

    private void OnPreviewNotificationClick(
        object sender,
        RoutedEventArgs e)
    {
        var privacyLevel = (EventPrivacyLevel)Math.Clamp(
            PrivacyLevelCombo.SelectedIndex,
            0,
            3);
        var wasShown =
            NotificationPreviewRequested?.Invoke(privacyLevel) == true;
        NotificationActionStatusText.Text = wasShown
            ? $"已发送{GetPrivacyLevelName(privacyLevel)}预览。"
            : "暂时无法显示预览，请确认灵动岛正在运行。";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SavedSettings = new AppSettings
        {
            MonitorTarget = MonitorTargetCombo.SelectedIndex == 1
                ? MonitorTargetMode.PrimaryDisplay
                : MonitorTargetMode.FollowActiveWindow,
            TopGap = TopGapSlider.Value,
            BackgroundTransparency = TransparencySlider.Value,
            EnableAnimations = EnableAnimationsCheckBox.IsChecked == true,
            HideInFullscreen = HideInFullscreenCheckBox.IsChecked == true,
            DoNotDisturb = DoNotDisturbCheckBox.IsChecked == true,
            MonitorAiActivity = AiMonitorCheckBox.IsChecked == true,
            TopStashed = _topStashed,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            LyricsFallbackProvider =
                LyricsFallbackProviderCombo.SelectedIndex == 1
                    ? LyricsFallbackProvider.QqMusic
                    : LyricsFallbackProvider.None,
            PrivacyLevel = (EventPrivacyLevel)Math.Clamp(
                PrivacyLevelCombo.SelectedIndex,
                0,
                3),
            NotificationAllowList = ParseSourceList(
                NotificationAllowListTextBox.Text),
            NotificationBlockList = ParseSourceList(
                NotificationBlockListTextBox.Text)
        }.Normalize();

        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        SavedSettings = null;
        Close();
    }

    private void UpdateTopGapText()
    {
        TopGapValueText.Text = $"{TopGapSlider.Value:0} DIP";
    }

    private static string[] ParseSourceList(string text)
    {
        return text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
    }

    private static string GetPrivacyLevelName(EventPrivacyLevel privacyLevel)
    {
        return privacyLevel switch
        {
            EventPrivacyLevel.Full => "完整内容",
            EventPrivacyLevel.Masked => "遮蔽内容",
            EventPrivacyLevel.IconOnly => "仅图标",
            _ => "摘要"
        };
    }
}
