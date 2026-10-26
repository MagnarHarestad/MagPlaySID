# Draws the package's tile images from docs/logo.png into Assets\, each the logo at the size
# Windows shows it, with a margin round it as Microsoft's tile guidelines ask. Run again only if
# the logo changes; the images are committed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$logo = [System.Drawing.Image]::FromFile((Join-Path $here '..\..\docs\logo.png'))

# Name, width, height, and how much of the shorter side the logo fills.
$tiles = @(
    @('Square44x44Logo.png', 44, 44, 0.82),
    @('Square44x44Logo.targetsize-44_altform-unplated.png', 44, 44, 0.82),
    @('Square150x150Logo.png', 150, 150, 0.66),
    @('Wide310x150Logo.png', 310, 150, 0.66),
    @('StoreLogo.png', 50, 50, 0.9)
)

foreach ($tile in $tiles) {
    $name, $width, $height, $share = $tile
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $side = [int][math]::Round([math]::Min($width, $height) * $share)
    $graphics.DrawImage($logo, [int](($width - $side) / 2), [int](($height - $side) / 2), $side, $side)
    $graphics.Dispose()
    $bitmap.Save((Join-Path $here "Assets\$name"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Assets\$name  ${width}x$height"
}

$logo.Dispose()
