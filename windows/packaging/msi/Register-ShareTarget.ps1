[CmdletBinding()]
param([string]$InstallRoot)

$ErrorActionPreference = 'Stop'
if (-not $InstallRoot) {
    $InstallRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$log = Join-Path $InstallRoot 'register-sharetarget.log'

function Log([string]$m) {
    Add-Content -Path $log -Value ("{0:O} {1}" -f [DateTimeOffset]::UtcNow, $m)
}

try {
    $msix = Join-Path $InstallRoot 'WeChatBridge.ShareTarget.msix'
    $certDir = Join-Path $InstallRoot 'certs'

    function Test-IsAdmin {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        return ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }

    # The MSIX signature must chain to a trusted root before Add-AppxPackage
    # accepts it. The dev certificate chain ships under certs\; import it when
    # missing.
    $signature = Get-AuthenticodeSignature -FilePath $msix
    if ($signature.Status -ne 'Valid') {
        $admin = Test-IsAdmin
        $leafCer = Join-Path $certDir 'WeChatBridge.Windows.Dev.cer'
        $rootCer = Join-Path $certDir 'WeChatBridge.Windows.Dev.Root.cer'
        if (Test-Path $leafCer) {
            Import-Certificate -FilePath $leafCer -CertStoreLocation Cert:\CurrentUser\TrustedPeople -Confirm:$false | Out-Null
            if ($admin) { Import-Certificate -FilePath $leafCer -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Confirm:$false | Out-Null }
        }
        if (Test-Path $rootCer) {
            Import-Certificate -FilePath $rootCer -CertStoreLocation Cert:\CurrentUser\Root -Confirm:$false | Out-Null
            if ($admin) { Import-Certificate -FilePath $rootCer -CertStoreLocation Cert:\LocalMachine\Root -Confirm:$false | Out-Null }
        }
        $signature = Get-AuthenticodeSignature -FilePath $msix
        if ($signature.Status -ne 'Valid') {
            throw "MSIX signature is not trusted: $($signature.Status)"
        }
    }

    Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $msix -ExternalLocation $InstallRoot
    $registered = Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
    if (-not $registered) { throw 'Package registration did not stick.' }
    Log "registered $($registered.PackageFullName) at $InstallRoot"
}
catch {
    Log "FAILED $($_.Exception.Message)"
    throw
}
