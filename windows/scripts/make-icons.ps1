# Repack the existing shared square artwork for Windows; no extra transparent padding.
param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
Add-Type -AssemblyName System.Drawing
$sourcePath = Join-Path $RepositoryRoot 'shared\Screenshots\app-icon-rounded.png'
$assetDirectory = Join-Path $RepositoryRoot 'windows\packaging\SparsePackage\Assets'
$iconPath = Join-Path $RepositoryRoot 'windows\src\WeChatBridge.Windows\Assets\AppIcon.ico'
$artwork = [Drawing.Image]::FromFile($sourcePath)
function Get-IconPng([int]$side) {
    $bitmap = [Drawing.Bitmap]::new($side, $side, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($artwork, [Drawing.Rectangle]::new(0, 0, $side, $side), 0, 0, $artwork.Width, $artwork.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally { $stream.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
try {
    # Preserve every scale / target-size filename and its packaging dimensions.
    foreach ($file in Get-ChildItem -LiteralPath $assetDirectory -Filter '*.png') {
        $existing = [Drawing.Image]::FromFile($file.FullName)
        $side = $existing.Width
        if ($side -ne $existing.Height) { $existing.Dispose(); throw "Icon must be square: $($file.Name)" }
        $existing.Dispose()
        [IO.File]::WriteAllBytes($file.FullName, (Get-IconPng $side))
    }
    $sizes = @(16, 24, 32, 48, 64, 256)
    $frames = @($sizes | ForEach-Object { ,(Get-IconPng $_) })
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
        $writer.Flush()
        [IO.File]::WriteAllBytes($iconPath, $stream.ToArray())
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $artwork.Dispose() }
Write-Output 'Updated Windows ICO and all MSIX icon sizes from the shared rounded artwork.'
