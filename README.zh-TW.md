# Windows Dynamic Capsule

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文**

Windows Dynamic Capsule 是一個面向 Windows 10/11 的開源桌面狀態膠囊。
它在螢幕頂部集中顯示媒體播放、同步歌詞、Windows 通知、本機工作進度、
下載狀態、倒數計時與碼錶，並提供全螢幕隱藏、手動頂部收納和隱私控制。

> 目前專案仍處於預覽階段。介面、設定格式和系統整合方式可能繼續調整。

## 主要功能

- 不搶焦點、置頂的 WPF 膠囊視窗；
- 系統媒體工作階段、封面、播放控制與可拖曳播放進度；
- 透過 LRCLIB 查詢逐行同步歌詞；
- 經使用者授權顯示 Windows 通知摘要，並提供獨立勿擾模式；
- 本機工作、瀏覽器下載、倒數計時、碼錶及 Windows 時鐘狀態；
- 多工作主次分區、多顯示器、高 DPI、全螢幕隱藏與頂部收納；
- 僅限目前使用者的 Named Pipe 工作事件介面。

## 環境需求

- Windows 10 2004（build 19041）或更新版本；
- .NET SDK `10.0.302`；
- PowerShell 5.1 或更新版本。

## 本機建置

```powershell
dotnet restore .\DynamicCapsule.slnx
dotnet build .\DynamicCapsule.slnx -c Release
dotnet run --project .\src\DynamicCapsule\DynamicCapsule.csproj
```

執行核心驗證：

```powershell
dotnet run --project .\tests\DynamicCapsule.CoreProbe\DynamicCapsule.CoreProbe.csproj -c Release
```

## MSIX 與 Microsoft Store

儲存庫只提供 Partner Center 身分範本，不公開維護者的本機提交設定、簽署
私密金鑰或憑證密碼。準備自己的 Store 套件時：

```powershell
Copy-Item `
  .\packaging\store-submission.template.json `
  .\packaging\store-submission.json
```

接著將本機檔案中的預留文字替換為你自己的 Partner Center 身分。該檔案已被
`.gitignore` 排除。完整流程請參閱
[packaging/STORE_SUBMISSION.md](packaging/STORE_SUBMISSION.md)。

本儲存庫對應的 Microsoft Store 產品身分由專案維護者控制。Fork、修改或
重新建置原始碼不會取得更新該 Store 產品的權限；第三方發布時必須使用自己的
應用程式名稱、套件身分、Publisher 和簽署資料。

## 隱私與安全性

通知內容、本機工作和使用者設定的處理方式請參閱 [PRIVACY.md](PRIVACY.md)。
安全性問題請依照 [SECURITY.md](SECURITY.md) 私下回報。公開截圖和範例資料
不得包含私人通知、存取權杖、未經授權的歌詞、專輯封面或桌布。

## 參與開發

提交修正或功能前請閱讀 [CONTRIBUTING.md](CONTRIBUTING.md)。完整產品範圍、
互動規則和技術設計請參閱 [PROJECT.md](PROJECT.md)。

## 授權條款

原始碼採用 [MIT License](LICENSE)。Windows Dynamic Capsule 是獨立的社群
專案，不受 Microsoft 或 Apple 贊助、認可或隸屬。第三方服務、商標和內容
仍受各自條款約束。
