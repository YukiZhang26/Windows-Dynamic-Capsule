# 公开仓库检查清单

## 已完成

- [x] 添加 MIT License。
- [x] 添加公开 README、贡献指南和安全报告说明。
- [x] 忽略构建产物、本地 Store 身份、环境文件和签名材料。
- [x] 保留无真实身份的 `store-submission.template.json`。
- [x] 从公开 Store 提交文档移除正式 Store ID、Publisher ID 和包哈希。
- [x] 隐私政策不再包含未替换的邮箱占位符。

## 公开仓库与安全设置

- [x] 仓库已公开：<https://github.com/YukiZhang26/Windows-Dynamic-Capsule>。
- [x] 已启用私密漏洞报告、Dependabot 安全更新、Secret Scanning 和
      Push Protection。
- [x] 已添加 Windows CI，并为 NuGet 依赖启用每周 Dependabot 检查。
- [x] 未使用强制添加方式提交被 `.gitignore` 排除的文件。
- [x] 已检查跟踪文件，其中没有 `.pfx`、`.p12`、`.key`、`.pem`、
      `.msix`、本机设置路径、GitHub 令牌或 Partner Center 私人截图。
- [ ] 仓库截图只使用自制或明确获准使用的图片、歌曲和歌词示例。
- [ ] 正式 Store 发布前将隐私政策部署到
      无需登录即可访问的 HTTPS 地址。
- [ ] 对公开 Release 的二进制进行可信代码签名；不要把本地开发证书当作
      面向用户的正式签名。

> 2026-08-09 本地候选包 `0.9.0.0` 已使用 Partner Center 正式包身份完成
> 测试证书签名、时间戳和 `0.1.0.59 → 0.9.0.0` 就地升级验证，设置哈希与
> 权限状态均保持正常。该测试证书只用于本机验收，不作为公开 Release 的
> 可信签名。`1.0.0.0` 未签名包只用于 Partner Center 提交并由 Store 签名，
> 不作为 GitHub 直装包发布。

## 每次发布前

- [ ] `dotnet build .\DynamicCapsule.slnx -c Release` 通过。
- [ ] CoreProbe 和相关 `scripts/verify-*.ps1` 检查通过。
- [ ] MSIX 版本号高于上次提交版本。
- [ ] `runFullTrust`、通知、网络访问和隐私说明与实际代码一致。
- [ ] 发布包和源码中没有调试日志、个人路径或私有配置。

以下项目不能由 CI 替代，仍需在发布候选包上人工验证：

- Windows 通知授权关闭与恢复。
- Windows 时钟计时器/秒表的实际同步与控制。
- 多显示器、不同 DPI、热插拔以及全屏切换。
- `verify-window.ps1` 的真实桌面窗口与截图验证。
- `verify-msix.ps1 -RequireSignature`、Windows App Certification Kit 和
  Partner Center 认证。
