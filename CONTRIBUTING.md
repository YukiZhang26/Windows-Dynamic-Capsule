# 参与贡献

感谢你改进 Windows Dynamic Capsule。

## 开始之前

1. 先在 Issue 中说明较大的功能或行为变更；小型修复可直接提交。
2. 不要提交真实通知、聊天内容、歌词、专辑封面、访问令牌或签名材料。
3. 不要提交 `packaging/store-submission.json`；Store 身份必须使用自己的
   本地配置。

## 开发流程

```powershell
dotnet restore .\DynamicCapsule.slnx
dotnet build .\DynamicCapsule.slnx -c Release
dotnet run --project .\tests\DynamicCapsule.CoreProbe\DynamicCapsule.CoreProbe.csproj -c Release
```

涉及窗口动画、DPI、媒体控制或系统通知的改动，还应在真实 Windows 环境
执行对应的 `scripts/verify-*.ps1` 检查，并在提交说明中记录系统版本、缩放
比例和验证结果。

## 提交要求

- 保持改动聚焦，并说明用户可见行为；
- 为可独立验证的业务逻辑补充或更新 CoreProbe 检查；
- 权限不可用时必须安全降级，不得绕过 Windows 安全策略；
- 新增网络请求时同步更新隐私政策；
- UI 必须兼顾键盘、UI Automation、减少动画和高对比度模式。
