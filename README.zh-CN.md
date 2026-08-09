# Windows Dynamic Capsule

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md)

Windows Dynamic Capsule 是一个面向 Windows 10/11 的开源桌面状态胶囊。
它在屏幕顶部集中显示媒体播放、同步歌词、Windows 通知、本地任务进度、
下载状态、倒计时与秒表，并提供全屏隐藏、手动顶部收纳和隐私控制。

> 当前项目仍处于预览阶段。界面、配置格式和系统集成方式可能继续调整。

## 主要功能

- 无焦点、置顶的 WPF 胶囊窗口；
- 系统媒体会话、封面、播放控制与可拖动播放进度；
- 通过 LRCLIB 查询逐行同步歌词，可选 QQ 音乐备用来源并提供 30 天本地缓存；
- 经用户授权显示 Windows 通知摘要，并提供独立勿扰模式；
- 本地任务、浏览器下载、倒计时、秒表及 Windows 时钟状态；
- 多任务主次分区、多显示器、高 DPI、全屏隐藏与顶部收纳；
- 仅限当前用户的 Named Pipe 任务事件接口。

## 环境要求

- Windows 10 2004（build 19041）或更高版本；
- .NET SDK `10.0.302`；
- PowerShell 5.1 或更高版本。

## 本地构建

```powershell
dotnet restore .\DynamicCapsule.slnx
dotnet build .\DynamicCapsule.slnx -c Release
dotnet run --project .\src\DynamicCapsule\DynamicCapsule.csproj
```

运行核心验证：

```powershell
dotnet run --project .\tests\DynamicCapsule.CoreProbe\DynamicCapsule.CoreProbe.csproj -c Release
```

## MSIX 与 Microsoft Store

仓库只提供 Partner Center 身份模板，不公开维护者的本地提交配置、签名
私钥或证书密码。准备自己的 Store 包时：

```powershell
Copy-Item `
  .\packaging\store-submission.template.json `
  .\packaging\store-submission.json
```

然后将本地文件中的占位符替换为你自己的 Partner Center 身份。该文件已被
`.gitignore` 排除。详细流程见
[packaging/STORE_SUBMISSION.md](packaging/STORE_SUBMISSION.md)。

公开二进制必须遵守[代码签名策略](CODE_SIGNING.md)中相互独立的 Store 与
直装渠道规则；未签名的 Store 候选包和本机测试签名包不会作为公开附件。

本仓库对应的 Microsoft Store 产品身份由项目维护者控制。Fork、修改或
重新构建源码不会获得更新该 Store 产品的权限；第三方发布时必须使用自己的
应用名称、包身份、Publisher 和签名材料。

## 隐私与安全

通知内容、本地任务和用户设置的处理方式见 [PRIVACY.md](PRIVACY.md)。
安全问题请按照 [SECURITY.md](SECURITY.md) 私下报告。公开截图和示例数据
不得包含私人通知、访问令牌、未经授权的歌词、专辑封面或壁纸。

## 参与开发

提交修复或功能前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。完整产品范围、
交互规则和技术设计见 [PROJECT.md](PROJECT.md)。

## 许可证

源代码采用 [MIT License](LICENSE)。Windows Dynamic Capsule 是独立的社区
项目，不受 Microsoft 或 Apple 赞助、认可或隶属。第三方服务、商标和内容
仍受各自条款约束。
