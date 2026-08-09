# Microsoft Store 发布（维护者指南）

当前推荐路径是把 MSIX 提交到 Microsoft Store，并将受众设置为私有。
Store 完成认证后会重新签名安装包，因此不需要在启用 Smart App
Control 的电脑上安装自签名根证书或关闭安全策略。

Store 与 GitHub 直装包的 Publisher 和升级链必须分开管理，详细门禁见
[../CODE_SIGNING.md](../CODE_SIGNING.md)。Partner Center 上传候选包不能作为
GitHub Release 附件。

## 当前准备状态

- [x] Release、x64、自包含 MSIX 构建。
- [x] EXE、DLL、清单、BlockMap 和包结构验证。
- [x] `runFullTrust` 与 `userNotificationListener` 能力声明。
- [x] Store 包使用 Windows `StartupTask` 管理登录启动。
- [x] Partner Center 身份配置模板和一键构建脚本。
- [x] 完成开发者账户身份验证。
- [x] 预留应用名称 `Windows Dynamic Capsule`。
- [x] 从 Partner Center 复制包身份。
- [x] 生成最终 Store 身份 MSIX。
- [x] 准备至少一张不含第三方版权素材的应用截图；本地候选为
      `artifacts/store/store-task-combination-1366x768-v2.png`（1366×768）。
- [ ] 合并发布 PR 后验证三语隐私政策 URL，并填写到对应 Store 页面。
- [ ] 设置私有受众并提交认证。

正式 Store ID、Package Identity、Publisher、提交包哈希和受众信息仅保存在
维护者的 Partner Center 与本地 `store-submission.json` 中，不写入公开仓库。
这些标识本身不是密码，但公开仓库不应把正式生产身份作为第三方构建默认值。

## 1. 注册并预留名称

1. 从 <https://storedeveloper.microsoft.com/> 注册个人开发者账户。
2. 在 Partner Center 的 **Apps and games** 工作区创建新产品。
3. 预留应用名称。建议优先尝试 `Windows Dynamic Capsule`；
   如果已被占用，可使用 `Dynamic Capsule for Windows`。
4. 创建产品后打开产品的 **Product identity / 产品标识** 页面。

不要自行编造包身份。请从该页面原样复制：

- Package/Identity/Name
- Package/Identity/Publisher
- Publisher display name

这些值不是密码或私钥，但必须与 Partner Center 完全一致。

## 2. 写入身份配置

复制模板：

```powershell
Copy-Item `
  .\packaging\store-submission.template.json `
  .\packaging\store-submission.json
```

`store-submission.json` 已被 `.gitignore` 排除。在其中替换三个
`PASTE_...` 值；不要提交该文件。随后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-store-msix.ps1
```

脚本会拒绝占位符、错误格式和超出 MSIX 范围的版本号，并检查生成包
中的身份是否与配置完全一致。输出仍标记为 `unsigned.msix`，这是预期
行为；正式签名由 Microsoft Store 在认证后完成。

### 使用正式包身份进行本机升级测试

不要直接调用通用 `build-msix.ps1` 并依赖它的开发默认身份，否则 Windows
会把测试包安装为另一个并行应用。需要验证 Store 身份的安装和升级时，使用：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-store-msix.ps1 `
  -LocalTestCertificateThumbprint "<40 位测试证书指纹>"
```

该模式仍从被忽略的 `store-submission.json` 读取确切身份，并强制检查生成
包的 Identity、Publisher、版本和签名状态。结果只用于本机安装/升级测试，
不得上传 Partner Center，也不得作为 GitHub Release 发布。

### 运行 Windows App Certification Kit

先下载并验证当前稳定版 Windows SDK 引导程序；此命令不会安装组件：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\prepare-wack.ps1
```

当前固定版本为 `10.0.28000.2526`。脚本会校验 SHA-256、产品版本和 Microsoft
Authenticode 签名。确认输出正常并准备进行交互式安装后，再显式执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\prepare-wack.ps1 `
  -LaunchInstaller
```

在 Windows SDK Setup 中只选择 **Windows App Certification Kit**，除非确实
需要其他 SDK 组件。脚本不会静默安装，也不会绕过 UAC。安装完成后，在活动
用户会话中打开管理员 PowerShell，然后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\run-wack.ps1
```

脚本只选择已安装的 `YukiZhang.WindowsDynamicCapsule` 包，先重置 WACK 状态，
再运行认证并把 XML 报告保存到 `artifacts\wack`。缺少管理员权限、WACK、
包身份或报告时会失败，不会把未执行认证误报为通过。

## 3. 隐私政策 URL

当前发布 PR 合并到 `main` 后，分别验证并使用以下地址：

- English: <https://github.com/YukiZhang26/Windows-Dynamic-Capsule/blob/main/PRIVACY.en.md>
- 简体中文: <https://github.com/YukiZhang26/Windows-Dynamic-Capsule/blob/main/PRIVACY.md>
- 繁體中文: <https://github.com/YukiZhang26/Windows-Dynamic-Capsule/blob/main/PRIVACY.zh-TW.md>

这些页面必须保持公开且无需登录；提交前用匿名浏览器再次验证 HTTP 200。

## 4. 私有受众

在提交的 **Pricing and availability / 定价和可用性** 页面：

1. 选择 **Private audience / 私有受众**。
2. 新建已知用户组。
3. 只添加需要安装应用的 Microsoft 账户邮箱。
4. 不设置自动转为公开受众的日期。
5. 价格选择免费。

私有受众必须在首次公开发布前设置。之后可以转为公开受众，但已公开
的产品不能再改回私有。

## 5. 权限用途说明草稿

### runFullTrust

> Windows Dynamic Capsule 是 WPF 桌面辅助工具。完整信任用于创建
> 无焦点置顶窗口、系统托盘菜单、本机用户范围 Named Pipe、媒体控制，
> 以及根据前台窗口和显示器边界处理全屏隐藏。应用不请求管理员权限，
> 不安装服务或驱动，不执行远程下载的代码。

### userNotificationListener

> 经用户在 Windows 权限对话框中明确授权后，应用读取当前用户的
> Toast 通知，在屏幕顶部短暂显示来源、标题和摘要。通知正文不会持久化
> 或上传；用户可以设置允许列表、屏蔽列表及 summary、masked、
> iconOnly 等隐私级别，也可随时在 Windows 设置中撤销权限。

### 网络访问

> 播放媒体时，应用可把歌曲标题、歌手、专辑名称和时长发送到
> `https://lrclib.net` 查询同步歌词。应用不创建账户、不上传通知内容，
> 也不包含广告或遥测。

## 6. 商店文案草稿

### 简短说明

适用于 Windows 的轻量顶部动态胶囊，集中呈现媒体播放、同步歌词、
系统通知、本地任务进度和倒计时，同时兼顾全屏场景与隐私控制。

### 主要功能

- 无焦点顶部胶囊，不打断当前工作。
- 媒体封面、播放控制和逐行同步歌词。
- 经授权显示 Windows 通知摘要。
- 本机任务进度和倒计时提醒。
- 自动避让全屏应用并支持多显示器。
- 通知来源过滤、勿扰模式和四级隐私显示。
- 高对比度、减少动画和键盘操作支持。

### 关键词

`dynamic capsule`、`media controls`、`lyrics`、`notifications`、
`timer`、`productivity`

## 7. 提交前不得遗漏

- 将 `PRIVACY.md` 中的联系邮箱占位符替换为真实地址。
- 把隐私政策发布到无需登录即可访问的 HTTPS 页面。
- 截图不能包含他人的私人通知、邮箱、令牌或未经授权的壁纸和专辑图。
- 首次提交使用 `0.1.0.0`；后续每次提交必须提高版本号。
- 上传包前再次执行 `verify-msix.ps1`，但不要要求本地签名。
