#!/usr/bin/env bash
# Builds a signed APK without the Android SDK/Gradle.
# Needs: JDK 17+ (javac), and the Ubuntu 24.04 packages: aapt dalvik-exchange zipalign apksigner
#   sudo apt-get install -y aapt dalvik-exchange zipalign apksigner
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"
SDK=.sdk
if [ ! -f $SDK/framework-res-34.apk ] || [ ! -f $SDK/android-compile-34.jar ]; then
  tools/fetch-sdk.sh
fi
B=build
rm -rf $B && mkdir -p $B/gen $B/classes
VERSION=$(grep -o 'android:versionName="[^"]*"' AndroidManifest.xml | cut -d'"' -f2)

echo "== aapt2: resources"
aapt2 compile --dir res -o $B/res.zip
aapt2 link -o $B/base.apk -I $SDK/framework-res-34.apk --manifest AndroidManifest.xml \
  -A assets --java $B/gen $B/res.zip

echo "== javac"
javac -proc:none --release 8 -encoding UTF-8 -Xlint:-options -nowarn \
  -cp $SDK/android-compile-34.jar -d $B/classes $(find src $B/gen -name '*.java')

echo "== dex"
dalvik-exchange --dex --min-sdk-version=26 --output=$B/classes.dex $B/classes

echo "== package, align, sign"
cp $B/base.apk $B/unsigned.apk
(cd $B && zip -q -X unsigned.apk classes.dex)
zipalign -f -p 4 $B/unsigned.apk $B/aligned.apk
if [ ! -f keystore/gymlog.jks ]; then
  echo "== keystore/gymlog.jks not found: creating a new signing key"
  mkdir -p keystore
  keytool -genkeypair -keystore keystore/gymlog.jks -storetype PKCS12 -storepass gymlog-key \
    -keypass gymlog-key -alias gymlog -keyalg RSA -keysize 2048 -validity 18250 \
    -dname "CN=Dnevnik Zala, O=Personal"
fi
apksigner sign --ks keystore/gymlog.jks --ks-pass pass:gymlog-key --key-pass pass:gymlog-key \
  --ks-key-alias gymlog --out $B/app.apk $B/aligned.apk
apksigner verify --min-sdk-version 26 $B/app.apk
mkdir -p release
OUT=release/dnevnik-zala-$VERSION.apk
cp $B/app.apk "$OUT"
echo "APK: $ROOT/$OUT ($(stat -c %s "$OUT") bytes)"
