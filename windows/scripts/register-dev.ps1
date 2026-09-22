[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallRoot)
$helper = Join-Path $installRoot 'share-target\WeChatBridge.ShareTarget.exe'
$manifestDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\packaging\SparsePackage'))
$packagePath = Join-Path $installRoot 'WeChatBridge.ShareTarget.msix'

if (-not (Test-Path $helper)) { throw "Share Target helper not found: $helper" }
if (-not (Test-Path (Join-Path $installRoot 'WeChatBridge.Windows.exe'))) { throw 'WPF host not found under InstallRoot.' }

function Resolve-SdkTool([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidate = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($candidate) { return $candidate.FullName }
    throw "$name was not found. Install the Windows SDK or add it to PATH."
}

if (-not $CertificateThumbprint) {
    $CertificateThumbprint = (Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
}
if (-not $CertificateThumbprint) { throw 'Run new-dev-certificate.ps1 first.' }

$makeAppx = Resolve-SdkTool 'makeappx.exe'
$signTool = Resolve-SdkTool 'signtool.exe'
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
& $makeAppx pack /d $manifestDir /p $packagePath /nv
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }
& $signTool sign /fd SHA256 /sha1 $CertificateThumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE." }

Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue |
    Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $packagePath -ExternalLocation $installRoot
$registered = Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
if (-not $registered) { throw 'Add-AppxPackage returned but the package is not registered.' }
Write-Output "Registered: $($registered.PackageFullName)"
