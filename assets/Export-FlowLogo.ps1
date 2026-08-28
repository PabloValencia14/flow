[CmdletBinding()]
param(
    [int]$Size = 512
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$assetsRoot = $PSScriptRoot
$windowsRoot = Join-Path $assetsRoot '..\Flow.Windows'
$pngPath = Join-Path $windowsRoot 'FlowLogo.png'
$icoPath = Join-Path $windowsRoot 'FlowLogo.ico'

Add-Type -AssemblyName System.Drawing.Common

function Add-RoundedRectangle {
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Graphics]$Graphics,
        [Parameter(Mandatory = $true)][System.Drawing.Brush]$Brush,
        [Parameter(Mandatory = $true)][double]$X,
        [Parameter(Mandatory = $true)][double]$Y,
        [Parameter(Mandatory = $true)][double]$Width,
        [Parameter(Mandatory = $true)][double]$Height,
        [Parameter(Mandatory = $true)][double]$Radius
    )

    $diameter = $Radius * 2
    $Graphics.FillRectangle($Brush, [single]($X + $Radius), [single]$Y, [single]($Width - $diameter), [single]$Height)
    $Graphics.FillRectangle($Brush, [single]$X, [single]($Y + $Radius), [single]$Width, [single]($Height - $diameter))
    $Graphics.FillEllipse($Brush, [single]$X, [single]$Y, [single]$diameter, [single]$diameter)
    $Graphics.FillEllipse($Brush, [single]$X, [single]($Y + $Height - $diameter), [single]$diameter, [single]$diameter)
    $Graphics.FillEllipse($Brush, [single]($X + $Width - $diameter), [single]$Y, [single]$diameter, [single]$diameter)
    $Graphics.FillEllipse($Brush, [single]($X + $Width - $diameter), [single]($Y + $Height - $diameter), [single]$diameter, [single]$diameter)
}

function New-FlowBitmap {
    param([int]$CanvasSize = $Size)

    $bitmap = [System.Drawing.Bitmap]::new($CanvasSize, $CanvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $black = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(16, 16, 16))
    $scale = $CanvasSize / 256.0

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        Add-RoundedRectangle $graphics $white (0 * $scale) (0 * $scale) (256 * $scale) (256 * $scale) (56 * $scale)
        foreach ($bar in @(
            @(24, 64, 24, 136),
            @(70, 124, 24, 76),
            @(116, 88, 24, 112),
            @(162, 124, 24, 76),
            @(208, 64, 24, 136)
        )) {
            Add-RoundedRectangle $graphics $black ($bar[0] * $scale) ($bar[1] * $scale) ($bar[2] * $scale) ($bar[3] * $scale) (12 * $scale)
        }
        return $bitmap
    }
    finally {
        $white.Dispose()
        $black.Dispose()
        $graphics.Dispose()
    }
}

$bitmap = New-FlowBitmap -CanvasSize $Size
try {
    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

    # Windows acepta una imagen PNG dentro de un contenedor ICO y conserva
    # correctamente la transparencia del símbolo en los tamaños modernos.
    $iconBitmap = New-FlowBitmap -CanvasSize ([Math]::Min($Size, 256))
    try {
        $pngStream = [System.IO.MemoryStream]::new()
        try {
            $iconBitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngBytes = $pngStream.ToArray()
        }
        finally {
            $pngStream.Dispose()
        }

        $stream = [System.IO.File]::Create($icoPath)
        try {
            $writer = [System.IO.BinaryWriter]::new($stream)
            try {
                $writer.Write([uint16]0)                         # Reserved
                $writer.Write([uint16]1)                         # Icon type
                $writer.Write([uint16]1)                         # Image count
                $writer.Write([byte]0)                            # Width: 256
                $writer.Write([byte]0)                            # Height: 256
                $writer.Write([byte]0)                            # Palette
                $writer.Write([byte]0)                            # Reserved
                $writer.Write([uint16]1)                          # Color planes
                $writer.Write([uint16]32)                         # Bits per pixel
                $writer.Write([uint32]$pngBytes.Length)
                $writer.Write([uint32]22)                         # Header + one entry
                $writer.Write($pngBytes)
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $iconBitmap.Dispose()
    }
}
finally {
    $bitmap.Dispose()
}

Write-Host "Logo generado: $pngPath"
Write-Host "Icono generado: $icoPath"
