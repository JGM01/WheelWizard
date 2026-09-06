#!/bin/bash
set -euo pipefail
native_dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$native_dir/../.." && pwd)
workspace=$(cd "${1:?Usage: build.sh /path/to/WiiCompiled}" && pwd)
artifacts="$native_dir/artifacts"
mkdir -p "$artifacts/tools"
[[ $(uname -m) == arm64 ]] || { echo 'Apple Silicon required' >&2; exit 1; }
# Pinned upstream release, including its published SHA-256.
nod="$artifacts/tools/nodtool"
if [[ ! -f "$nod" ]]; then
  curl --fail --location --retry 3 https://github.com/encounter/nod/releases/download/v2.0.0-alpha.10/nodtool-macos-arm64 -o "$nod.tmp"
  mv "$nod.tmp" "$nod"
fi
[[ $(shasum -a 256 "$nod" | awk '{print $1}') == e23ca466999b720c55e6d29c9683fce8cc74451ba64ead2e543d50129f24528a ]] || { echo 'nodtool checksum mismatch' >&2; exit 1; }
chmod +x "$nod"
"$nod" --version
# Each publish directory contains its own runtime. Neither executable uses system dotnet.
dotnet publish "$workspace/translator/src/Translator.Cli/Translator.Cli.csproj" -c Release -r osx-arm64 --self-contained true -m:1 -p:CSharpier_Bypass=true -p:PublishTrimmed=false -p:PublishAot=false -o "$artifacts/tools/translator"
dotnet publish "$repo/WheelWizard.Host/WheelWizard.Host.csproj" -c Release -r osx-arm64 --self-contained true -m:1 -p:CSharpier_Bypass=true -p:PublishTrimmed=false -p:PublishAot=false -o "$artifacts/helper"
xcodebuild -project "$native_dir/WheelWizardNative.xcodeproj" -scheme WheelWizardNative -configuration Release -derivedDataPath "$artifacts/xcode" build
app="$artifacts/WheelWizardNative.app"
rm -rf "$app"
ditto "$artifacts/xcode/Build/Products/Release/WheelWizardNative.app" "$app"
ditto "$artifacts/helper" "$app/Contents/Resources/helper"
ditto "$artifacts/tools" "$app/Contents/Resources/tools"
cp "$repo/LICENSE" "$app/Contents/Resources/WheelWizard-LICENSE"
cp "$workspace/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/THIRD-PARTY-NOTICES.md"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
printf '\nBuilt %s\nRun: open "%s"\n' "$app" "$app"
