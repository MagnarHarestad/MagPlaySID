#!/bin/bash
# Builds MagSID.app for one Mac architecture and zips it.
#   packaging/macos/package.sh osx-arm64 0.2
# Runs on macOS (GitHub's macos runners): it needs sips, iconutil, codesign and ditto.
#
# Signing follows what the environment has:
#   MACOS_SIGN_IDENTITY       a "Developer ID Application: ..." identity in the keychain - signs with
#                             the hardened runtime, as notarisation requires. Without it: ad hoc.
#   APPLE_API_KEY_PATH,       an App Store Connect API key (.p8), its id and issuer - notarises the
#   APPLE_API_KEY_ID,         signed app and staples the ticket, so Gatekeeper opens it without
#   APPLE_API_ISSUER          asking. Only with a Developer ID signature.
set -euo pipefail

rid="$1"
version="$2"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/out/$rid"
app="$out/MagSID.app"
zip="$out/MagSID-$version-macos-${rid#osx-}.zip"

rm -rf "$out"
dotnet publish "$root/src/MagSid.Editor.Avalonia" -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -p:Version="$version" -o "$out/publish"

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

identity="${MACOS_SIGN_IDENTITY:-}"
if [ -n "$identity" ]; then
  # Every file in Contents/MacOS is signed on its own - the .NET assemblies sit beside the native
  # code there - and the bundle last, so its seal covers them all.
  entitlements="$root/packaging/macos/MagSID.entitlements"
  find "$app/Contents/MacOS" -type f | while read -r file; do
    codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$file"
  done
  codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app"
  codesign --verify --strict --verbose=2 "$app"
else
  # Apple Silicon will not run a binary with no signature at all, so without a Developer ID the
  # bundle is signed ad hoc. The first launch needs Open Anyway in Privacy & Security (macOS 15),
  # right-click, Open (macOS 14), or the quarantine mark removed - see the release notes.
  echo "No MACOS_SIGN_IDENTITY: signing ad hoc, not notarised."
  codesign --force --deep --sign - "$app"
fi

ditto -c -k --keepParent "$app" "$zip"

if [ -n "$identity" ] && [ -n "${APPLE_API_KEY_PATH:-}" ]; then
  xcrun notarytool submit "$zip" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" \
    --issuer "$APPLE_API_ISSUER" --wait
  # The ticket goes into the app itself, so it opens offline too; then zip it again with it.
  xcrun stapler staple "$app"
  rm "$zip"
  ditto -c -k --keepParent "$app" "$zip"
  spctl --assess --type execute --verbose "$app"
fi

echo "$zip"
