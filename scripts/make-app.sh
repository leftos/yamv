#!/usr/bin/env bash
# Assembles YAMV.app from a macOS folder published by scripts/publish.ps1 and signs it ad hoc.
#
# Usage: scripts/make-app.sh <publish dir> <out dir>
#   e.g. scripts/make-app.sh publish/osx-arm64 dist   ->   dist/YAMV.app
# Runs on macOS only (codesign). The version comes from Directory.Build.props.
set -euo pipefail

if [[ $# -ne 2 || -z $1 || -z $2 ]]; then
    echo "usage: $0 <publish dir> <out dir>" >&2
    exit 2
fi

publish_dir=$1
out_dir=$2
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
icns="$root/src/Yamv.App/Assets/yamv.icns"
props="$root/Directory.Build.props"

if [[ ! -f "$publish_dir/yamv" ]]; then
    echo "error: $publish_dir/yamv does not exist; publish first: pwsh scripts/publish.ps1 -Rid osx-arm64" >&2
    exit 1
fi
if [[ ! -f "$icns" ]]; then
    echo "error: $icns does not exist; build it with tools/dev/make-icons.py" >&2
    exit 1
fi

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$props" | head -n 1)
if [[ -z "$version" ]]; then
    echo "error: no <Version> found in $props" >&2
    exit 1
fi

app="$out_dir/YAMV.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish_dir"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/yamv"
cp "$icns" "$app/Contents/Resources/yamv.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>YAMV</string>
    <key>CFBundleDisplayName</key>
    <string>YAMV</string>
    <key>CFBundleIdentifier</key>
    <string>dev.leftos.yamv</string>
    <key>CFBundleExecutable</key>
    <string>yamv</string>
    <key>CFBundleIconFile</key>
    <string>yamv</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Markdown document</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>net.daringfireball.markdown</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST

codesign --force --deep -s - "$app"
echo "Built $app ($version)"
