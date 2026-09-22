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
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null

Write-Output "Subject: $($certificate.Subject)"
Write-Output "Thumbprint: $($certificate.Thumbprint)"
Write-Output "Certificate store: Cert:\CurrentUser\My"
