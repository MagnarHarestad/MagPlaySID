# Builds the editor as an MSIX package for the Microsoft Store: published self-contained, so
# nothing else has to be installed, laid out with the manifest and the tile images, and packed
# with the Windows SDK's makeappx. The Store signs what it is sent; -CertificatePfx signs the
# package here as well, for installing it on this machine before it is submitted.
#
#   .\build-msix.ps1                       a package with a local test identity
#   .\build-msix.ps1 -CertificatePfx x.pfx a signed one, to install by double-clicking
#
# The Store identity - Name, Publisher, PublisherDisplayName - is in store-identity.json beside
# this script once the app's name is reserved in Partner Center (Product identity page); without
# it the package gets a test identity the Store would refuse.
param(
    [string]$CertificatePfx,
    [string]$CertificatePassword
)
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
$out = Join-Path $here 'out'
$publish = Join-Path $out 'publish'
$layout = Join-Path $out 'layout'

$identity = @{ Name = 'MagSID.Editor.LocalTest'; Publisher = 'CN=MagSID Local Test'; PublisherDisplayName = 'Magnar Harestad' }
$identityFile = Join-Path $here 'store-identity.json'
if (Test-Path $identityFile) {
    $stored = Get-Content $identityFile -Raw | ConvertFrom-Json
    $identity = @{ Name = $stored.Name; Publisher = $stored.Publisher; PublisherDisplayName = $stored.PublisherDisplayName }
} else {
    Write-Warning 'No store-identity.json: building with a local test identity, not one the Store accepts.'
}

# The editor's <Version>, 1.63, as the four numbers a package needs: 1.63.0.0. The Store wants
# the last one 0, and every update a higher version than the last - so 1.63 must be followed by
# 1.64 or 2.0, never 1.7.
$csproj = [xml](Get-Content (Join-Path $root 'src\MagSid.Editor.App\MagSid.Editor.App.csproj'))
$appVersion = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
$parts = @($appVersion.Split('.')) + @('0', '0', '0')
$version = ($parts[0..2] -join '.') + '.0'

$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' | Sort-Object FullName | Select-Object -Last 1
if (-not $sdk) { throw 'makeappx.exe not found - install the Windows SDK.' }
$makeappx = $sdk.FullName
$signtool = Join-Path $sdk.DirectoryName 'signtool.exe'

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout | Out-Null

Write-Host "Publishing MagSID Editor $appVersion, self-contained..."
& dotnet publish (Join-Path $root 'src\MagSid.Editor.App\MagSid.Editor.App.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $publish -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The program and the native libraries .NET puts beside it; not the debugging symbols.
Get-ChildItem $publish -File | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $layout
Copy-Item (Join-Path $here 'Assets') -Destination (Join-Path $layout 'Assets') -Recurse
foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $file) -Destination $layout
}

$manifest = Get-Content (Join-Path $here 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('$NAME$', $identity.Name).Replace('$PUBLISHER$', $identity.Publisher)
$manifest = $manifest.Replace('$PUBLISHERDISPLAYNAME$', $identity.PublisherDisplayName).Replace('$VERSION$', $version)
Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding UTF8

$package = Join-Path $out "MagSID-Editor-$version-x64.msix"
& $makeappx pack /d $layout /p $package /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }

if ($CertificatePfx) {
    $arguments = @('sign', '/fd', 'SHA256', '/f', $CertificatePfx)
    if ($CertificatePassword) { $arguments += @('/p', $CertificatePassword) }
    & $signtool @arguments $package
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed - the certificate''s subject must be the manifest''s Publisher.' }
}

$size = [math]::Round((Get-Item $package).Length / 1MB, 1)
Write-Host "Built $package ($size MB), identity $($identity.Name), publisher $($identity.Publisher), version $version"
