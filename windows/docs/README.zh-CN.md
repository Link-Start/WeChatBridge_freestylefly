# 微信流 Windows PoC

这是与 macOS 版本共仓库维护的 Windows 迁移起点。

第一阶段只验证：

```text
微信 Windows → Share Target → ZIP → Inbox → FileDropList → 最小 WPF 主程序
```

当前不包含自动粘贴、OCR、Agent 适配、Obsidian、技能管理或正式发布。

## 本地构建

```powershell
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnet restore windows\WeChatBridge.Windows.sln
& $dotnet build windows\WeChatBridge.Windows.sln -c Release
& $dotnet test windows\WeChatBridge.Windows.sln -c Release
```

## 本地发布布局

```text
<install-root>\WeChatBridge.Windows.exe
<install-root>\share-target\WeChatBridge.ShareTarget.exe
```

随后运行：

```powershell
windows\scripts\new-dev-certificate.ps1
windows\scripts\register-dev.ps1 -InstallRoot <install-root>
```

如果注册时出现 `0x800B0109`，打开当前用户的“受信任的根证书颁发机构”，导入脚本输出的
`WeChatBridge.Windows.Dev.cer`，然后重新运行注册脚本。注册脚本会把失败的 HResult 写入
`<install-root>\registration.log`。

## SignPath Foundation 签名

正式测试包使用仓库根目录的 `.github/workflows/windows-sign.yml`，通过 GitHub Actions 手动触发。
在 GitHub 仓库中配置以下内容：

- Secret：`SIGNPATH_API_TOKEN`
- Variables：`SIGNPATH_ORGANIZATION_ID`、`SIGNPATH_PROJECT_SLUG`、`SIGNPATH_SIGNING_POLICY_SLUG`
- Variable：`SIGNPATH_MSIX_PUBLISHER`，必须与 SignPath 证书 Subject 完全一致

SignPath 项目创建完成后，工作流会先构建和测试，再上传未签名 MSIX，提交 SignPath 签名请求，最后上传签名后的 MSIX。`pack-msix.ps1` 会在签名前注入 Publisher 和版本号。
