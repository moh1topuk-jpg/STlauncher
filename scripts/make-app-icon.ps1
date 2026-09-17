#Requires -Version 5.1
<#
.SYNOPSIS
    Turns Assets/server-logo.png into the application icon.

.DESCRIPTION
    Windows executables need an .ico. This script converts the launcher artwork into
    Assets/app.ico and makes sure the project references it, so the built exe carries
    the logo as well as the window.

    The window icon is loaded straight from the PNG at runtime, so the logo shows up
    even without running this script. Run it to also brand the .exe file itself.

.EXAMPLE
    .\scripts\make-app-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\STlauncher.App\Assets\server-logo.png')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source)) {
    throw "Logo not found at $Source. Save the image there first."
}

Add-Type -AssemblyName System.Drawing

$assets = Split-Path $Source -Parent
$target = Join-Path $assets 'app.ico'

$bitmap = [System.Drawing.Image]::FromFile($Source)

try {
    # An .ico file is a small header plus PNG or BMP frames; a single 256x256 frame is
    # enough here and keeps the script dependency-free.
    $size = 256
    $square = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($square)

    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($bitmap, 0, 0, $size, $size)
    }
    finally {
        $graphics.Dispose()
    }

    $pngStream = New-Object System.IO.MemoryStream
    $square.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $pngStream.ToArray()
    $pngStream.Dispose()
    $square.Dispose()

    $file = [System.IO.File]::Create($target)

    try {
        $writer = New-Object System.IO.BinaryWriter $file

        # ICONDIR
        $writer.Write([UInt16]0)     # reserved
        $writer.Write([UInt16]1)     # type: icon
        $writer.Write([UInt16]1)     # image count

        # ICONDIRENTRY: width and height 0 mean 256
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([UInt16]1)     # colour planes
        $writer.Write([UInt16]32)    # bits per pixel
        $writer.Write([UInt32]$pngBytes.Length)
        $writer.Write([UInt32]22)    # offset of the image data
        $writer.Write($pngBytes)
    }
    finally {
        $file.Dispose()
    }
}
finally {
    $bitmap.Dispose()
}

Write-Host "Icon written to $target"

$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\STlauncher.App\STlauncher.App.csproj'
$content = Get-Content -LiteralPath $project -Raw

if ($content -notmatch '<ApplicationIcon>') {
    $content = $content -replace '(<Nullable>enable</Nullable>)', "`$1`r`n    <ApplicationIcon>Assets\app.ico</ApplicationIcon>"
    Set-Content -LiteralPath $project -Value $content -Encoding UTF8
    Write-Host 'Added <ApplicationIcon> to the project.'
}
else {
    Write-Host '<ApplicationIcon> is already set.'
}
