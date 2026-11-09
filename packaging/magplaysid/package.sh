#!/bin/bash
# Builds MagPlaySID for Windows or Linux as one self-contained executable and packs it.
#   packaging/magplaysid/package.sh win-x64 0.1    -> out/win-x64/MagPlaySID-0.1-windows-x64.zip
#   packaging/magplaysid/package.sh linux-x64 0.1  -> out/linux-x64/MagPlaySID-0.1-linux-x64.tar.gz
# Nothing to install first: .NET is inside, and the native libraries (Skia, PortAudio) unpack
# themselves on first start. The Mac app is package-macos.sh's.
set -euo pipefail

rid="$1"
version="$2"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/out/$rid"
dir="$out/MagPlaySID-$version"

# The code comes compiled ahead of time: the window and the first sound in half the time, on a
# first start most of all, for a quarter more to download.
ahead=true

# On Windows the native libraries - Skia, HarfBuzz, ANGLE, PortAudio - sit beside the exe: packed
# inside it, each new version unpacked them into %TEMP%\.net on its first start, slowly, and left
# them there. On Linux they stay inside, so the one file can be installed alone (see README.txt).
unpack=true
case "$rid" in win-*) unpack=false ;; esac

rm -rf "$out"
dotnet publish "$root/src/MagPlaySID" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=$unpack -p:PublishReadyToRun=$ahead \
  -p:DebugType=none -p:Version="$version" -o "$dir"

cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$dir/"

case "$rid" in
  win-*)
    package="$out/MagPlaySID-$version-windows-${rid#win-}.zip"
    (cd "$out" && 7z a -tzip -bso0 "$package" "MagPlaySID-$version" >/dev/null)
    ;;
  linux-*)
    # A launcher entry and the icon, for whoever wants it in their menu (see README.txt).
    cp "$root/docs/logo.png" "$dir/magplaysid.png"
    cat > "$dir/magplaysid.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=MagPlaySID
Comment=Plays C64 .sid tunes and shows them as tracker rows
Exec=MagPlaySID %f
Icon=magplaysid
Categories=AudioVideo;Audio;Player;
MimeType=audio/prs.sid;
DESKTOP
    cat > "$dir/README.txt" <<README
MagPlaySID $version for Linux (${rid#linux-})

Run ./MagPlaySID, or ./MagPlaySID tune.sid to play one - or drop a .sid on the window.
Sound goes through PortAudio (ALSA, or PulseAudio/PipeWire through ALSA).

To have it in the desktop menu:
  install -Dm755 MagPlaySID ~/.local/bin/MagPlaySID
  install -Dm644 magplaysid.png ~/.local/share/icons/hicolor/512x512/apps/magplaysid.png
  install -Dm644 magplaysid.desktop ~/.local/share/applications/magplaysid.desktop
README
    chmod +x "$dir/MagPlaySID"
    package="$out/MagPlaySID-$version-linux-${rid#linux-}.tar.gz"
    tar -C "$out" -czf "$package" "MagPlaySID-$version"
    ;;
  *)
    echo "Unknown runtime $rid" >&2
    exit 1
    ;;
esac

echo "$package"
