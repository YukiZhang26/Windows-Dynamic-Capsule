param(
    [ValidateRange(1, 30)]
    [int]$DurationSeconds = 4
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class FullscreenTestDpi
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
}
'@

[FullscreenTestDpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Dynamic Capsule Fullscreen Test'
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form.BackColor = [System.Drawing.Color]::FromArgb(15, 18, 24)
$form.ForeColor = [System.Drawing.Color]::White
$form.ShowInTaskbar = $true
$form.TopMost = $true
$form.KeyPreview = $true

$label = New-Object System.Windows.Forms.Label
$label.Dock = [System.Windows.Forms.DockStyle]::Fill
$label.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$label.Font = New-Object System.Drawing.Font('Segoe UI', 28)
$label.Text = "Fullscreen suppression test`r`nThis window closes automatically."
$form.Controls.Add($label)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $DurationSeconds * 1000
$timer.Add_Tick({
    $timer.Stop()
    $form.Close()
})

$form.Add_Shown({
    $form.Activate()
    $form.BringToFront()
    $timer.Start()
})
$form.Add_KeyDown({
    if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
        $form.Close()
    }
})

try {
    [System.Windows.Forms.Application]::Run($form)
}
finally {
    $timer.Dispose()
    $label.Dispose()
    $form.Dispose()
}
