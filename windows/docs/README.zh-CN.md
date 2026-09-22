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
