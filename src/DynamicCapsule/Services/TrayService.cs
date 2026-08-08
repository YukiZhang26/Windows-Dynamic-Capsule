using DynamicCapsule.Models;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicCapsule.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _doNotDisturbItem;
    private readonly Forms.ToolStripMenuItem _countdownPauseItem;
    private readonly Forms.ToolStripMenuItem _countdownCancelItem;
    private readonly Forms.ToolStripMenuItem _stopwatchPauseItem;
    private readonly Forms.ToolStripMenuItem _stopwatchStopItem;
    private readonly Forms.ToolStripMenuItem _pinItem;
    private bool _isPaused;
    private bool _isDoNotDisturbEnabled;
    private bool _disposed;

    internal TrayService()
    {
        _pauseItem = new Forms.ToolStripMenuItem("关闭胶囊(&P)");
        _pauseItem.Click += OnPauseItemClick;

        var toggleCapsuleItem =
            new Forms.ToolStripMenuItem("展开/收起胶囊(&O)");
        toggleCapsuleItem.Click += OnToggleCapsuleItemClick;

        var settingsItem = new Forms.ToolStripMenuItem("设置(&S)…");
        settingsItem.Click += OnSettingsItemClick;

        _doNotDisturbItem = new Forms.ToolStripMenuItem(
            "胶囊勿扰（独立于 Windows）(&D)");
        _doNotDisturbItem.Click += OnDoNotDisturbItemClick;

        var countdownMenu =
            new Forms.ToolStripMenuItem("新建灵动岛计时器(&T)");
        countdownMenu.DropDownItems.Add(
            CreateCountdownItem("5 分钟", 5));
        countdownMenu.DropDownItems.Add(
            CreateCountdownItem("15 分钟", 15));
        countdownMenu.DropDownItems.Add(
            CreateCountdownItem("25 分钟", 25));
        countdownMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        var customCountdownItem = new Forms.ToolStripMenuItem("自定义…");
        customCountdownItem.Click += (_, _) => CountdownRequested?.Invoke(null);
        countdownMenu.DropDownItems.Add(customCountdownItem);

        _countdownPauseItem = new Forms.ToolStripMenuItem("暂停计时器")
        {
            Enabled = false
        };
        _countdownPauseItem.Click += OnCountdownPauseItemClick;

        _countdownCancelItem = new Forms.ToolStripMenuItem("取消计时器")
        {
            Enabled = false
        };
        _countdownCancelItem.Click += OnCountdownCancelItemClick;

        var stopwatchMenu = new Forms.ToolStripMenuItem("秒表(&W)");
        var stopwatchStartItem = new Forms.ToolStripMenuItem("启动秒表");
        stopwatchStartItem.Click += (_, _) =>
            StopwatchStartRequested?.Invoke();
        stopwatchMenu.DropDownItems.Add(stopwatchStartItem);

        _stopwatchPauseItem = new Forms.ToolStripMenuItem("暂停秒表")
        {
            Enabled = false
        };
        _stopwatchPauseItem.Click += OnStopwatchPauseItemClick;
        stopwatchMenu.DropDownItems.Add(_stopwatchPauseItem);

        _stopwatchStopItem = new Forms.ToolStripMenuItem("终止秒表")
        {
            Enabled = false
        };
        _stopwatchStopItem.Click += OnStopwatchStopItemClick;
        stopwatchMenu.DropDownItems.Add(_stopwatchStopItem);

        _pinItem = new Forms.ToolStripMenuItem("固定当前事件")
        {
            Enabled = false
        };
        _pinItem.Click += OnPinItemClick;

        var diagnosticsItem = new Forms.ToolStripMenuItem("诊断状态(&G)…");
        diagnosticsItem.Click += OnDiagnosticsItemClick;

        var exitItem = new Forms.ToolStripMenuItem("退出(&X)");
        exitItem.Click += OnExitItemClick;

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.Add(toggleCapsuleItem);
        _contextMenu.Items.Add(_pauseItem);
        _contextMenu.Items.Add(_doNotDisturbItem);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(countdownMenu);
        _contextMenu.Items.Add(_countdownPauseItem);
        _contextMenu.Items.Add(_countdownCancelItem);
        _contextMenu.Items.Add(stopwatchMenu);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(_pinItem);
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(diagnosticsItem);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = SystemIcons.Application,
            Text = "Windows Dynamic Capsule",
            Visible = true
        };
        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
    }

    internal event Action<bool>? PauseChanged;
    internal event Action? CapsuleToggleRequested;
    internal event Action<bool>? DoNotDisturbChanged;
    internal event Action<int?>? CountdownRequested;
    internal event Action? CountdownPauseResumeRequested;
    internal event Action? CountdownCancelRequested;
    internal event Action? StopwatchStartRequested;
    internal event Action? StopwatchPauseResumeRequested;
    internal event Action? StopwatchStopRequested;
    internal event Action? ActiveEventPinToggleRequested;
    internal event Action? SettingsRequested;
    internal event Action? DiagnosticsRequested;
    internal event Action? ExitRequested;

    internal void UpdatePaused(bool isPaused)
    {
        _isPaused = isPaused;
        _pauseItem.Checked = isPaused;
        _pauseItem.Text = isPaused
            ? "启动胶囊(&P)"
            : "关闭胶囊(&P)";
        _notifyIcon.Text = isPaused
            ? "Windows Dynamic Capsule（已关闭）"
            : "Windows Dynamic Capsule";
    }

    internal void UpdateDoNotDisturb(bool isEnabled)
    {
        _isDoNotDisturbEnabled = isEnabled;
        _doNotDisturbItem.Checked = isEnabled;
    }

    internal void UpdateCountdown(CountdownTimerStatus status)
    {
        _countdownPauseItem.Enabled = status.IsActive;
        _countdownCancelItem.Enabled = status.IsActive;
        _countdownPauseItem.Text = status.IsPaused
            ? "继续计时器"
            : "暂停计时器";

        if (status.IsActive)
        {
            var remaining = FormatRemaining(status.Remaining);
            _countdownPauseItem.Text =
                $"{_countdownPauseItem.Text}（{remaining}）";
        }
    }

    internal void UpdateStopwatch(StopwatchStatus status)
    {
        _stopwatchPauseItem.Enabled = status.IsActive;
        _stopwatchStopItem.Enabled = status.IsActive;
        _stopwatchPauseItem.Text = status.IsPaused
            ? "继续秒表"
            : "暂停秒表";

        if (status.IsActive)
        {
            _stopwatchPauseItem.Text =
                $"{_stopwatchPauseItem.Text}"
                + $"（{StopwatchService.FormatElapsed(status.Elapsed)}）";
        }
    }

    internal void UpdateActiveEvent(bool hasActiveEvent, bool isPinned)
    {
        _pinItem.Enabled = hasActiveEvent;
        _pinItem.Checked = isPinned;
        _pinItem.Text = isPinned
            ? "取消固定当前事件"
            : "固定当前事件";
    }

    internal void ShowStatus(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        _pauseItem.Click -= OnPauseItemClick;
        _doNotDisturbItem.Click -= OnDoNotDisturbItemClick;
        _countdownPauseItem.Click -= OnCountdownPauseItemClick;
        _countdownCancelItem.Click -= OnCountdownCancelItemClick;
        _stopwatchPauseItem.Click -= OnStopwatchPauseItemClick;
        _stopwatchStopItem.Click -= OnStopwatchStopItemClick;
        _pinItem.Click -= OnPinItemClick;
        _contextMenu.Dispose();
    }

    private void OnPauseItemClick(object? sender, EventArgs e)
    {
        UpdatePaused(!_isPaused);
        PauseChanged?.Invoke(_isPaused);
    }

    private void OnToggleCapsuleItemClick(object? sender, EventArgs e)
    {
        CapsuleToggleRequested?.Invoke();
    }

    private void OnSettingsItemClick(object? sender, EventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnDoNotDisturbItemClick(object? sender, EventArgs e)
    {
        UpdateDoNotDisturb(!_isDoNotDisturbEnabled);
        DoNotDisturbChanged?.Invoke(_isDoNotDisturbEnabled);
    }

    private void OnCountdownPauseItemClick(object? sender, EventArgs e)
    {
        CountdownPauseResumeRequested?.Invoke();
    }

    private void OnCountdownCancelItemClick(object? sender, EventArgs e)
    {
        CountdownCancelRequested?.Invoke();
    }

    private void OnStopwatchPauseItemClick(object? sender, EventArgs e)
    {
        StopwatchPauseResumeRequested?.Invoke();
    }

    private void OnStopwatchStopItemClick(object? sender, EventArgs e)
    {
        StopwatchStopRequested?.Invoke();
    }

    private void OnPinItemClick(object? sender, EventArgs e)
    {
        ActiveEventPinToggleRequested?.Invoke();
    }

    private void OnExitItemClick(object? sender, EventArgs e)
    {
        ExitRequested?.Invoke();
    }

    private void OnDiagnosticsItemClick(object? sender, EventArgs e)
    {
        DiagnosticsRequested?.Invoke();
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private Forms.ToolStripMenuItem CreateCountdownItem(
        string text,
        int minutes)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => CountdownRequested?.Invoke(minutes);
        return item;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var remainingSeconds = seconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes:00}:{remainingSeconds:00}";
    }
}
