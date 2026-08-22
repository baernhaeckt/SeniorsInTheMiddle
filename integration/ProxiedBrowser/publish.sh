#!/usr/bin/env bash
#
# Publishes DemoBrowser as one self-contained macOS application bundle: publish/Demo Browser.app
#
# Recreates the ignored publish/ folder next to this script. The bundle contains the .NET runtime, the app,
# the Chromium Embedded Framework (libcef + resources) and the CefGlueBrowserProcess helper — nothing else
# has to be installed on the target Mac. Requires the .NET 10 SDK (and Xcode command line tools for codesign).
#
# The bundle is x64 (CefGlue ships CEF for macOS x64 only); on Apple Silicon it runs under Rosetta 2.
set -euo pipefail

cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
    echo 'The .NET SDK is not on PATH. Install .NET 10 from https://dotnet.microsoft.com/download' >&2
    exit 1
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo 'publish.sh must run on macOS: the .app bundle is assembled with native tools.' >&2
    exit 1
fi

PROJECT="DemoBrowser/DemoBrowser.csproj"
OUTPUT="$PWD/publish"
STAGING="$PWD/DemoBrowser/obj/bundle"
RID="osx-x64"

rm -rf "$OUTPUT" "$STAGING"
mkdir -p "$OUTPUT"

# Dotnet.Bundle's BundleApp target publishes the project and wraps the publish output into
# "<CFBundleDisplayName>.app" (Info.plist from the CFBundle* properties in the .csproj).
dotnet msbuild "$PROJECT" \
    -restore \
    -t:BundleApp \
    -p:Configuration=Release \
    -p:RuntimeIdentifier="$RID" \
    -p:SelfContained=true \
    -p:PublishDir="$STAGING/" \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:CopyOutputSymbolsToPublishDirectory=false \
    -p:AllowedReferenceRelatedFileExtensions=none

APP="$(find "$STAGING" -maxdepth 1 -name '*.app' -type d | head -n 1)"
if [[ -z "$APP" ]]; then
    echo "dotnet msbuild did not produce an .app bundle in $STAGING" >&2
    exit 1
fi

mv "$APP" "$OUTPUT/"
APP="$OUTPUT/$(basename "$APP")"
MACOS="$APP/Contents/MacOS"

# Dotnet.Bundle writes CFBundleIconFile into Info.plist but does not copy the icon itself.
mkdir -p "$APP/Contents/Resources"
cp DemoBrowser/Assets/app.icns "$APP/Contents/Resources/app.icns"

# Executables lose their mode bits on the way through NuGet; restore them (same as the CefGlue samples).
chmod +x "$MACOS/DemoBrowser"
chmod +x "$MACOS/CefGlueBrowserProcess/Xilium.CefGlue.BrowserProcess"
find "$MACOS" -name '*.dylib' -exec chmod +x {} +

# Sanity check: the CEF runtime must sit next to the executable (CefGlue loads it from there).
for required in "$MACOS/libcef.dylib" "$MACOS/Resources/icudtl.dat" "$MACOS/CefGlueBrowserProcess/Xilium.CefGlue.BrowserProcess" \
                "$APP/Contents/Info.plist" "$APP/Contents/Resources/app.icns"; do
    if [[ ! -e "$required" ]]; then
        echo "Publish output is incomplete: missing $required" >&2
        exit 1
    fi
done

# Ad-hoc signature so Gatekeeper on Apple Silicon lets the (unsigned) native code run at all.
if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$APP" >/dev/null
fi

rm -rf "$STAGING"

SIZE_MB="$(du -sm "$APP" | cut -f1)"
echo "Published $APP (${SIZE_MB} MB)"
echo 'First launch of an unsigned app: right-click > Open, or run: xattr -dr com.apple.quarantine "publish/Demo Browser.app"'
