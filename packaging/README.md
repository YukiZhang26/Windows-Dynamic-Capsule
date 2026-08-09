# MSIX 发布与签名

本目录提供可重复的 MSIX 发布流程。流水线不会创建证书、导入私钥、修改证书信任或关闭 Windows 应用控制策略。

## 1. 生成未签名验证包

在仓库根目录执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-msix.ps1
```

首次执行会把以下官方依赖恢复到仓库内已忽略的 `.nuget` 目录：

- `Microsoft.Windows.SDK.BuildTools 10.0.28000.2526`
- .NET 10 `win-x64` 自包含运行时包

输出位于 `artifacts\msix`：

- `WindowsDynamicCapsule_<version>_x64.unsigned.msix`
- 同名 `.metadata.json`
- `work\layout`：打包前布局
- `work\validation`：从生成包重新解包的验证内容

未签名包仅用于检查结构，不能作为正式安装包分发。

## 2. 预检代码签名证书

签名前只读检查证书，不会使用私钥：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\test-code-signing-certificate.ps1 `
  -CertificateThumbprint "<40 位证书指纹>" `
  -RequireTrustedChain
```

证书必须：

- 位于当前用户的 `Cert:\CurrentUser\My`；
- 带有可访问的私钥；
- 包含 Code Signing EKU `1.3.6.1.5.5.7.3.3`；
- 当前有效；
- 主题名称与 MSIX 清单的 `Publisher` 完全一致；
- 被目标机器的 Code Integrity 策略信任。

证书链受 Windows 信任并不自动意味着企业 Code Integrity 策略会接受它，最终仍需在目标策略环境中验证。

## 3. 生成签名 MSIX

证书获得批准后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-msix.ps1 `
  -PackageVersion "1.0.0.0" `
  -CertificateThumbprint "<40 位证书指纹>"
```

未显式传入 `Publisher` 时，脚本会使用证书的完整 Subject。若显式传入但与证书不完全相同，签名会在修改文件前停止。

签名顺序：

1. 发布自包含 `win-x64` 应用；
2. 签名应用自有的 EXE 和 DLL；
3. 用 `MakeAppx` 创建 SHA-256 MSIX；
4. 签名并为 MSIX 添加 RFC 3161 时间戳；
5. 重新解包检查身份、架构、入口文件和图标；
6. 使用 `SignTool verify /pa /all /v` 验签；
7. 写出 SHA-256 和无密钥发布元数据。

脚本仅支持安装在证书库中的证书指纹，不接受明文 PFX 密码，避免密码进入命令历史和构建日志。

## 4. 独立检查生成包

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-msix.ps1 `
  -PackagePath ".\artifacts\msix\WindowsDynamicCapsule_1.0.0.0_x64.msix" `
  -RequireSignature
```

## 5. 本机安装与升级预检

本机测试签名包在请求管理员权限前，应先运行只读升级预检：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\install-local-test-msix.ps1 `
  -CertificatePath "<公开测试证书 .cer>" `
  -PackagePath "<测试签名 .msix>" `
  -ExpectedThumbprint "<40 位测试证书指纹>" `
  -ResultPath ".\artifacts\msix\install-preflight.json" `
  -PreflightOnly
```

该模式只读取包、同名元数据、已安装版本和设置哈希，结果中必须为
`passed=true` 与 `systemStateModified=false`。预检通过后，才在管理员
PowerShell 中移除 `-PreflightOnly` 执行安装。脚本禁止降级和隐式同版本覆盖，
并在安装后核对确切版本、Publisher 与设置文件哈希。

## 6. 当前测试机限制

当前测试机的企业 Code Integrity 策略 ID 为：

```text
{0283ac0f-fff1-49ae-ada1-8a933130cad6}
```

它要求企业级签名。不要通过关闭策略、安装不明根证书或复用其他项目证书解决。应使用以下之一：

- 企业管理员签发并加入该策略允许范围的代码签名证书；
- Microsoft Store 对提交的 MSIX 重新签名；
- 符合组织条件的 Azure Artifact Signing；
- 受目标策略信任的商业代码签名证书。

参考：

- [Microsoft：MSIX 签名概览](https://learn.microsoft.com/windows/msix/package/signing-package-overview)
- [Microsoft：使用 MakeAppx 创建包](https://learn.microsoft.com/windows/msix/package/create-app-package-with-makeappx-tool)
- [Microsoft：使用 SignTool 签名 MSIX](https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool)
