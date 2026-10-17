#!/bin/bash
# 맥에 설치할 앱(YouTube Lyric Notch.app)과 설치용 디스크 이미지(.dmg)를 만든다.
# 실행: ./release.sh   →   release/YouTube Lyric Notch.app, release/YouTubeLyricNotch-<버전>.dmg
# Apple Silicon·Intel 둘 다 돌아가는 universal 앱. 개발자 인증서가 없어 ad-hoc 서명이라,
# 다른 맥에서 받은 dmg는 처음 열 때 우클릭 > 열기(또는 시스템 설정 > 개인정보 보호 및 보안 > 그래도 열기)가 필요하다.
set -euo pipefail
cd "$(dirname "$0")"

VERSION=1.2.0
NAME="YouTube Lyric Notch"
# 조립·서명은 임시 폴더에서: Documents가 iCloud로 동기화되면 앱 폴더에 Finder 정보가 다시 붙어 서명 검증이 깨진다
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
APP="$WORK/$NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

for arch in arm64 x86_64; do
    swiftc -O -target "$arch-apple-macos14" main.swift -o "$WORK/notch-$arch"
done
lipo -create "$WORK/notch-arm64" "$WORK/notch-x86_64" -output "$APP/Contents/MacOS/notch"
"$APP/Contents/MacOS/notch" --selftest

cp AppIcon.icns "$APP/Contents/Resources/"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>com.hongje.youtube-notch</string>
    <key>CFBundleName</key><string>$NAME</string>
    <key>CFBundleDisplayName</key><string>$NAME</string>
    <key>CFBundleExecutable</key><string>notch</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSAppleEventsUsageDescription</key><string>Chrome에서 재생 중인 유튜브의 곡과 시간을 읽고, 재생·이전·다음 버튼을 누르려고 Chrome을 제어해요.</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist" >/dev/null
xattr -cr "$APP"
codesign --force --sign - "$APP"
codesign --verify --deep --strict "$APP"

# dmg: 앱과 Applications 바로가기를 넣어 끌어다 놓으면 설치되게
mkdir "$WORK/dmg"
ditto "$APP" "$WORK/dmg/$NAME.app"
ln -s /Applications "$WORK/dmg/Applications"
rm -rf release && mkdir release
hdiutil create -volname "$NAME" -srcfolder "$WORK/dmg" -ov -format UDZO "release/YouTubeLyricNotch-$VERSION.dmg" >/dev/null
ditto "$APP" "release/$NAME.app"  # 바로 열어 볼 수 있게(설치는 dmg로)

echo "만듦: release/$NAME.app"
echo "만듦: release/YouTubeLyricNotch-$VERSION.dmg"
