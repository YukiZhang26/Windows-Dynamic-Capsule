# 公开仓库检查清单

## 已完成

- [x] 添加 MIT License。
- [x] 添加公开 README、贡献指南和安全报告说明。
- [x] 忽略构建产物、本地 Store 身份、环境文件和签名材料。
- [x] 保留无真实身份的 `store-submission.template.json`。
- [x] 从公开 Store 提交文档移除正式 Store ID、Publisher ID 和包哈希。
- [x] 隐私政策不再包含未替换的邮箱占位符。

## 创建公开仓库时

- [ ] 启用私密漏洞报告和依赖安全提醒。
- [ ] 不要使用强制添加方式提交被 `.gitignore` 排除的文件。
- [ ] 首次提交前检查待提交文件中没有 `.pfx`、`.p12`、`.key`、`.pem`、
      `.msix`、真实通知、账号截图或 Partner Center 私人页面截图。
- [ ] 仓库截图只使用自制或明确获准使用的图片、歌曲和歌词示例。
- [ ] 在仓库主页填写实际项目 URL；正式 Store 发布前将隐私政策部署到
      无需登录即可访问的 HTTPS 地址。
- [ ] 对公开 Release 的二进制进行可信代码签名；不要把本地开发证书当作
      面向用户的正式签名。

## 每次发布前

- [ ] `dotnet build .\DynamicCapsule.slnx -c Release` 通过。
- [ ] CoreProbe 和相关 `scripts/verify-*.ps1` 检查通过。
- [ ] MSIX 版本号高于上次提交版本。
- [ ] `runFullTrust`、通知、网络访问和隐私说明与实际代码一致。
- [ ] 发布包和源码中没有调试日志、个人路径或私有配置。
