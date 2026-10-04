[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage -Name 'ChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
if ($package) {
    $package | Remove-AppxPackage
    Write-Output "Unregistered: $($package.PackageFullName)"
} else {
    Write-Output 'Package is not registered.'
}
