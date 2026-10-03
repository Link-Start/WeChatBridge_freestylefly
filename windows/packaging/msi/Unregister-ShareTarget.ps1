[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue |
    Remove-AppxPackage -ErrorAction SilentlyContinue
