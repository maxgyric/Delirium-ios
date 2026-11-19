#!/usr/bin/env bash
# Runs inside unityci/editor:ubuntu-6000.3.28f1-android-3 (the repo at /project): takes a Unity Personal seat with
# your Unity login, builds the Quest's .apk (BuildAndroid, from ci/android), gives the seat back.
set -uo pipefail
LC=/opt/unity/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client

back() { "$LC" --deactivate-all --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" > /dev/null 2>&1 || true; }

out=$("$LC" --activate-all --include-personal --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" 2>&1)
code=$?
echo "$out" | tail -15
if [ $code -ne 0 ] || ! grep -q "Activation processed successfully" <<< "$out"; then
  echo "::error::Unity licence: activation failed (check UNITY_EMAIL / UNITY_PASSWORD)"
  exit 1
fi
trap back EXIT

unity-editor -batchmode -nographics -quit -projectPath /project -buildTarget Android \
  -executeMethod BuildAndroid.Build -customBuildPath /project/build/Android -logFile /project/unity-build.log
code=$?
grep -E "error CS|Exception|CommandInvokationFailure" /project/unity-build.log | tail -40
echo "---- BuildAndroid"; grep -E "^BuildAndroid: " /project/unity-build.log
# the manifest the headset reads: VR intent category, OpenXR runtime
m=$(find /project/Library/Bee/Android -name AndroidManifest.xml -path "*launcher*" 2>/dev/null | head -1)
echo "---- $m"; [ -n "$m" ] && grep -E "category|meta-data|uses-feature|intent" "$m" | head -30
[ -f /project/build/Android/DeliriumSandboxVR.apk ] || { echo "::error::no .apk (exit $code)"; exit 1; }
exit $code
