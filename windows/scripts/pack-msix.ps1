[CmdletBinding()]
param(
    [string]$Publisher = 'CN=WeChatBridge Windows Dev',
    [string]$Version = '0.1.0.0',
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($output)
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\packaging\SparsePackage'))
$stageDirectory = Join-Path $outputDirectory "SparsePackage-$([Guid]::NewGuid().ToString('N'))"

if ([string]::IsNullOrWhiteSpace($Publisher)) { throw 'Publisher must not be empty.' }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid MSIX version: $Version" }
if (-not (Test-Path (Join-Path $sourceDirectory 'AppxManifest.xml'))) { throw 'Sparse package manifest was not found.' }

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
Get-ChildItem -Path $sourceDirectory -Force | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination (Join-Path $stageDirectory $_.Name) -Recurse -Force
}

$manifestPath = Join-Path $stageDirectory 'AppxManifest.xml'
[xml]$manifest = Get-Content -Raw $manifestPath
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Package.Identity.Version = $Version
$manifest.Save($manifestPath)

function Resolve-SdkTool([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidate = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($candidate) { return $candidate.FullName }
    throw "$name was not found. Install the Windows SDK or add it to PATH."
}

$makeAppx = Resolve-SdkTool 'makeappx.exe'
& $makeAppx pack /d $stageDirectory /p $output /nv /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }
Write-Output "Packed: $output"
Write-Output "Publisher: $Publisher"
Write-Output "Version: $Version"
