#!/bin/bash
# Rebuild the WheelWizard native frontend app: compile Swift, publish the .NET helper,
# reassemble the bundle and ad-hoc sign it. Run from anywhere.
#
# Usage: rebuild-native.sh [/path/to/WiiCompiled]
# The optional WiiCompiled path only supplies THIRD-PARTY-NOTICES.md; skip it if not needed.
set -euo pipefail

dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$dir/../.." && pwd)
artifacts="$dir/artifacts"
app="$artifacts/WheelWizardNative.app"
workspace=${1:-$(cd "$repo/../Wiicompiled" 2>/dev/null && pwd)}

xcodebuild -project "$dir/WheelWizardNative.xcodeproj" -scheme WheelWizardNative \
  -configuration Release -destination 'platform=macOS' \
  -derivedDataPath "$artifacts/xcode" build

dotnet publish "$repo/WheelWizard.Host/WheelWizard.Host.csproj" \
  -c Release -r osx-arm64 --self-contained true \
  -p:CSharpier_Bypass=true -p:PublishTrimmed=false -p:PublishAot=false \
  -o "$artifacts/helper"

rm -rf "$app"
ditto "$artifacts/xcode/Build/Products/Release/WheelWizardNative.app" "$app"
ditto "$artifacts/helper" "$app/Contents/Resources/helper"
ditto "$artifacts/tools" "$app/Contents/Resources/tools"
cp "$repo/LICENSE" "$app/Contents/Resources/WheelWizard-LICENSE"
if [[ -f "$workspace/THIRD-PARTY-NOTICES.md" ]]; then
  cp "$workspace/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/THIRD-PARTY-NOTICES.md"
fi

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
printf '\nBuilt %s\nRun: open "%s"\n' "$app" "$app"
