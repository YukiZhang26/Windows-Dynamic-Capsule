namespace DynamicCapsule.Models;

internal sealed record AccessibilityPreferences(
    bool HighContrast,
    bool ReduceMotion)
{
    internal string Summary =>
        $"高对比度：{(HighContrast ? "开启" : "关闭")} · "
        + $"系统减少动画：{(ReduceMotion ? "开启" : "关闭")}";
}
