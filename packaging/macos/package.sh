#!/bin/bash
# Builds MagSID.app for one Mac architecture and zips it.
#   packaging/macos/package.sh osx-arm64 0.1
# Runs on macOS (GitHub's macos runners): it needs sips, iconutil, codesign and ditto.
set -euo pipefail

rid="$1"
version="$2"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/out/$rid"
app="$out/MagSID.app"

rm -rf "$out"
dotnet publish "$root/src/MagSid.Editor.Avalonia" -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -o "$out/publish"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$out/publish/." "$app/Contents/MacOS/"
sed "s/VERSION/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"

# The icon, from the website's logo, at every size macOS asks for.
iconset="$out/MagSID.iconset"
mkdir -p "$iconset"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$root/docs/logo.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$root/docs/logo.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/MagSID.icns"

# Not notarised - there is no Apple Developer ID yet - but Apple Silicon will not run a binary
# with no signature at all, so the bundle is signed ad hoc. First launch: right-click, Open.
codesign --force --deep --sign - "$app"

ditto -c -k --keepParent "$app" "$out/MagSID-macos-${rid#osx-}.zip"
echo "$out/MagSID-macos-${rid#osx-}.zip"
