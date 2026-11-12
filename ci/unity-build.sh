#!/usr/bin/env bash
# Runs inside unityci/editor:ubuntu-6000.3.28f1-ios-3 (the repo at /project): takes a Unity Personal seat with your
# Unity login (Unity 6's Hub makes no .ulf file to hand over), builds the Xcode project, gives the seat back.
set -uo pipefail
LC=/opt/unity/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client

back() { "$LC" --deactivate-all --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" > /dev/null 2>&1 || true; }

out=$("$LC" --activate-all --include-personal --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" 2>&1)
code=$?
echo "$out" | tail -15
if [ $code -ne 0 ] || ! grep -q "Activation processed successfully" <<< "$out"; then
  echo "::error::Unity licence: activation failed (check UNITY_EMAIL / UNITY_PASSWORD; an account with two-step verification can't sign in here)"
  exit 1
fi
trap back EXIT

unity-editor -batchmode -nographics -quit -projectPath /project -buildTarget iOS \
  -executeMethod BuildIOS.Build -customBuildPath /project/build/iOS/DeliriumSandbox -logFile /project/unity-build.log
code=$?
grep -E "error CS|BuildIOS:|Exception|Build (Finished|completed)" /project/unity-build.log | tail -60
[ -d /project/build/iOS/DeliriumSandbox/Unity-iPhone.xcodeproj ] || { echo "::error::no Xcode project (exit $code)"; exit 1; }
exit $code
