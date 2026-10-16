#!/bin/bash
# Builds the editor for Windows or Linux as one self-contained executable and packs it.
#   packaging/portable/package.sh win-x64 0.2      -> out/win-x64/MagSID-0.2-windows-x64.zip
#   packaging/portable/package.sh linux-x64 0.2    -> out/linux-x64/MagSID-0.2-linux-x64.tar.gz
# Nothing to install first: .NET is inside, and the native libraries (Skia, PortAudio) unpack
# themselves on first start.
set -euo pipefail

rid="$1"
version="$2"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/out/$rid"
dir="$out/MagSID-$version"

rm -rf "$out"
dotnet publish "$root/src/MagSid.Editor.Avalonia" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:Version="$version" -o "$dir"

cp "$root/LICENSE" "$dir/"

case "$rid" in
  win-*)
    package="$out/MagSID-$version-windows-${rid#win-}.zip"
    (cd "$out" && 7z a -tzip -bso0 "$package" "MagSID-$version" >/dev/null)
    ;;
  linux-*)
    # A launcher entry and the icon, for whoever wants it in their menu (see README.txt).
    cp "$root/docs/logo.png" "$dir/magsid.png"
    cat > "$dir/magsid.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=MagSID
Comment=SID music tracker and editor
Exec=MagSID %f
Icon=magsid
Categories=AudioVideo;Audio;Music;
MimeType=audio/prs.sid;
DESKTOP
    cat > "$dir/README.txt" <<README
MagSID $version for Linux (${rid#linux-})

Run ./MagSID, or ./MagSID song.s to open a song, ./MagSID tune.sid to play one.
Sound goes through PortAudio (ALSA, or PulseAudio/PipeWire through ALSA).

To have it in the desktop menu:
  install -Dm755 MagSID ~/.local/bin/MagSID
  install -Dm644 magsid.png ~/.local/share/icons/hicolor/256x256/apps/magsid.png
  install -Dm644 magsid.desktop ~/.local/share/applications/magsid.desktop
README
    chmod +x "$dir/MagSID"
    package="$out/MagSID-$version-linux-${rid#linux-}.tar.gz"
    tar -C "$out" -czf "$package" "MagSID-$version"
    ;;
  *)
    echo "Unknown runtime $rid" >&2
    exit 1
    ;;
esac

echo "$package"
