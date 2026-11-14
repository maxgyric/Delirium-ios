#!/usr/bin/env bash
# CI only: Unity's fileIDs for a few test models (ufbx's test data, MIT), written to fileids.txt.
set -uo pipefail
LC=/opt/unity/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client
back() { "$LC" --deactivate-all --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" > /dev/null 2>&1 || true; }
out=$("$LC" --activate-all --include-personal --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" 2>&1)
grep -q "Activation processed successfully" <<< "$out" || { echo "$out" | tail; echo "::error::activation failed"; exit 1; }
trap back EXIT
unity-editor -batchmode -nographics -quit -projectPath /project -executeMethod FileIdProbe.Run -logFile /project/probe.log
grep -E "error CS|FileIdProbe|Exception" /project/probe.log | tail -20
[ -s /project/fileids.txt ]
