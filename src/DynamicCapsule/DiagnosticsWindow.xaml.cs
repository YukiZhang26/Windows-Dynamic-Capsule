using DynamicCapsule.Models;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace DynamicCapsule;

public partial class DiagnosticsWindow : Window
{
    internal DiagnosticsWindow(DiagnosticsSnapshot snapshot)
    {
        InitializeComponent();
        ProcessText.Text = snapshot.Process;
        MediaText.Text = snapshot.Media;
        NotificationText.Text = snapshot.Notifications;
        TaskPipeText.Text = snapshot.TaskPipe;
        TimerText.Text = snapshot.Timer;
        ActiveEventText.Text = snapshot.ActiveEvent;
        ConfigurationText.Text = snapshot.Configuration;
        StartupText.Text = snapshot.Startup;
        AccessibilityText.Text = snapshot.Accessibility;
    }

    internal void UpdateAccessibility(string summary)
    {
        AccessibilityText.Text = summary;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildSummary());
            CopyStatusText.Text = "摘要已复制";
        }
        catch (ExternalException)
        {
            CopyStatusText.Text = "剪贴板暂时不可用";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnCopyClick(this, new RoutedEventArgs());
        }
    }

    private string BuildSummary()
    {
        var summary = new StringBuilder();
        AppendSummary(summary, "进程与实例", ProcessText.Text);
        AppendSummary(summary, "媒体", MediaText.Text);
        AppendSummary(summary, "Windows 通知", NotificationText.Text);
        AppendSummary(summary, "本地任务管道", TaskPipeText.Text);
        AppendSummary(summary, "计时器", TimerText.Text);
        AppendSummary(summary, "当前事件", ActiveEventText.Text);
        AppendSummary(summary, "配置", ConfigurationText.Text);
        AppendSummary(summary, "登录启动", StartupText.Text);
        AppendSummary(summary, "辅助功能", AccessibilityText.Text);
        return summary.ToString().TrimEnd();
    }

    private static void AppendSummary(
        StringBuilder summary,
        string label,
        string value)
    {
        summary.Append(label);
        summary.Append("：");
        summary.AppendLine(value);
    }
}
