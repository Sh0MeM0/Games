#!/usr/bin/env bash
# Prepares the Android 14 (API 34) platform files used by build.sh:
#   .sdk/framework-res-34.apk   - framework resources for `aapt2 link -I`
#   .sdk/android-compile-34.jar - framework classes for `javac -cp`
# Source: Robolectric "android-all" jar from Maven Central (built from AOSP by Google's
# Robolectric team). Used because dl.google.com (the normal Android SDK host) may be blocked.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK="$ROOT/.sdk"
V=14-robolectric-10818077
SHA1=94b1490a891e9be559aa35c87cd8a0c163f32d83
URL="https://repo1.maven.org/maven2/org/robolectric/android-all/$V/android-all-$V.jar"
JAR="$SDK/android-all-$V.jar"
mkdir -p "$SDK"
if [ ! -f "$JAR" ] || ! echo "$SHA1  $JAR" | sha1sum -c --quiet - 2>/dev/null; then
  curl -fSL --retry 4 --retry-delay 10 -o "$JAR" "$URL"
fi
echo "$SHA1  $JAR" | sha1sum -c -
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
(cd "$TMP" && unzip -q -o "$JAR" resources.arsc AndroidManifest.xml \
  && rm -f "$SDK/framework-res-34.apk" && zip -q -0 "$SDK/framework-res-34.apk" AndroidManifest.xml resources.arsc)
mkdir "$TMP/classes"
(cd "$TMP/classes" && unzip -q "$JAR" 'android/*' 'com/android/*' 'dalvik/*' 'org/json/*' 'libcore/*' \
  && rm -f "$SDK/android-compile-34.jar" && zip -q -r "$SDK/android-compile-34.jar" .)
echo "SDK ready in $SDK"
