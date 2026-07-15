#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
output_dir="${1:-$root_dir/artifacts/inprocess/net40}"
csc="${CSC_NET40:-/mnt/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe}"

if [[ ! -x "$csc" ]]; then
  echo "Windows .NET Framework csc.exe not found: $csc" >&2
  exit 1
fi

mkdir -p "$output_dir"
win_output="$(wslpath -w "$output_dir")"
loader="$(wslpath -w "$root_dir/InProcess/Loader/LocalCatchAgentDomainManager.cs")"
entry="$(wslpath -w "$root_dir/InProcess/Plugin/Entry.cs")"
settings="$(wslpath -w "$root_dir/InProcess/Plugin/AgentSettings.cs")"
planner="$(wslpath -w "$root_dir/InProcess/Plugin/RuntimeCatchPlanner.cs")"
agent="$(wslpath -w "$root_dir/InProcess/Plugin/LiveAgent.cs")"
overlay="$(wslpath -w "$root_dir/InProcess/Plugin/AgentOverlay.cs")"
planner_test="$(wslpath -w "$root_dir/InProcess/TestHost/PlannerProgram.cs")"
metadata_probe="$(wslpath -w "$root_dir/InProcess/TestHost/MetadataProbeProgram.cs")"

"$csc" /nologo /target:library /platform:anycpu /optimize+ \
  "/out:$win_output\\LocalCatchAgent.Loader.dll" "$loader"
"$csc" /nologo /target:library /platform:anycpu /optimize+ \
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll \
  "/out:$win_output\\LocalCatchAgent.Plugin.dll" \
  "$entry" "$settings" "$planner" "$agent" "$overlay"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalCatchAgent.PlannerTest.exe" \
  "$planner_test" "$planner" "$settings"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalCatchAgent.MetadataProbe.exe" "$metadata_probe"

# Windows csc writes through the WSL UNC bridge and does not preserve +x.
chmod +x \
  "$output_dir/LocalCatchAgent.PlannerTest.exe" \
  "$output_dir/LocalCatchAgent.MetadataProbe.exe"

(
  cd "$output_dir"
  # Only redistributable runtime artifacts are committed. Test executables are
  # rebuilt locally by the verification chain and intentionally stay untracked.
  sha256sum LocalCatchAgent.Loader.dll LocalCatchAgent.Plugin.dll > SHA256SUMS
)

echo "built Catch net40 artifacts in: $output_dir"
