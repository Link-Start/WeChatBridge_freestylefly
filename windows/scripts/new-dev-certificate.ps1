[CmdletBinding()]
param(
    [string]$Subject = 'CN=WeChatBridge Windows Dev'
)

$ErrorActionPreference = 'Stop'
$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -eq $Subject |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if ($existing -and $existing.NotAfter -gt (Get-Date).AddDays(30)) {
    $certificate = $existing
} else {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2)
}

$cer = Join-Path $env:TEMP 'WeChatBridge.Windows.Dev.cer'
Export-Certificate -Cert $certificate -FilePath $cer -Force | Out-Null
$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object Thumbprint -eq $certificate.Thumbprint
if (-not $trusted) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople -Confirm:$false | Out-Null
}

Write-Output "Subject: $($certificate.Subject)"
Write-Output "Thumbprint: $($certificate.Thumbprint)"
Write-Output "Certificate store: Cert:\CurrentUser\My"
Write-Output "Exported public certificate: $cer"
Write-Output "If Add-AppxPackage reports 0x800B0109, trust this .cer in the current-user Root store, then rerun register-dev.ps1."
