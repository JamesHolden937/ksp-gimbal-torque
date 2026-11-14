#!/usr/bin/env bash
# Build both plugins with Mono's mcs against a KSP 1.12.5 install.
#   KSP=/path/to/KSP ./build.sh
# Output: bin/GimbalProbe.dll (no dependencies)
#         bin/GimbalTorqueFix.dll (needs GameData/000_Harmony/0Harmony.dll)
set -euo pipefail
: "${KSP:?set KSP to the KSP 1.12.5 directory}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
M="$KSP/KSP_x64_Data/Managed"
REFS=(-r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll"
      -r:"$M/Assembly-CSharp.dll" -r:"$M/UnityEngine.CoreModule.dll" -r:"$M/UnityEngine.dll" -r:"$M/UnityEngine.UI.dll")
mkdir -p "$HERE/bin"
mcs -optimize -nologo -target:library -nostdlib -noconfig "${REFS[@]}" \
    -out:"$HERE/bin/GimbalProbe.dll" "$HERE/probe/GimbalProbe.cs"
mcs -optimize -nologo -target:library -nostdlib -noconfig "${REFS[@]}" \
    -r:"$KSP/GameData/000_Harmony/0Harmony.dll" \
    -out:"$HERE/bin/GimbalTorqueFix.dll" "$HERE/fix/GimbalTorqueFix.cs"
echo "built $HERE/bin/GimbalProbe.dll $HERE/bin/GimbalTorqueFix.dll"
