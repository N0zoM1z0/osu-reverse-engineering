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
loader="$(wslpath -w "$root_dir/InProcess/Loader/LocalTaikoAgentDomainManager.cs")"
entry="$(wslpath -w "$root_dir/InProcess/Plugin/Entry.cs")"
plan="$(wslpath -w "$root_dir/InProcess/Plugin/LivePlanBuilder.cs")"
timing_policy="$(wslpath -w "$root_dir/InProcess/Plugin/InputTimingPolicy.cs")"
settings="$(wslpath -w "$root_dir/InProcess/Plugin/AgentSettings.cs")"
humanizer="$(wslpath -w "$root_dir/InProcess/Plugin/Humanizer.cs")"
agent="$(wslpath -w "$root_dir/InProcess/Plugin/LiveAgent.cs")"
overlay="$(wslpath -w "$root_dir/InProcess/Plugin/AgentOverlay.cs")"
plan_test="$(wslpath -w "$root_dir/InProcess/TestHost/PlanProgram.cs")"
corpus_test="$(wslpath -w "$root_dir/InProcess/TestHost/CorpusProgram.cs")"
metadata_probe="$(wslpath -w "$root_dir/InProcess/TestHost/MetadataProbeProgram.cs")"

"$csc" /nologo /target:library /platform:anycpu /optimize+ \
  "/out:$win_output\\LocalTaikoAgent.Loader.dll" "$loader"
"$csc" /nologo /target:library /platform:anycpu /optimize+ \
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll \
  "/out:$win_output\\LocalTaikoAgent.Plugin.dll" \
  "$entry" "$plan" "$timing_policy" "$settings" "$humanizer" "$agent" "$overlay"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalTaikoAgent.PlanTest.exe" \
  "$plan_test" "$plan" "$timing_policy" "$settings" "$humanizer"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalTaikoAgent.CorpusTest.exe" \
  "$corpus_test" "$plan" "$timing_policy" "$settings" "$humanizer"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalTaikoAgent.MetadataProbe.exe" "$metadata_probe"

# csc.exe writes through the WSL UNC bridge, which does not preserve an executable bit.
chmod +x \
  "$output_dir/LocalTaikoAgent.PlanTest.exe" \
  "$output_dir/LocalTaikoAgent.CorpusTest.exe" \
  "$output_dir/LocalTaikoAgent.MetadataProbe.exe"

(
  cd "$output_dir"
  sha256sum LocalTaikoAgent.Loader.dll LocalTaikoAgent.Plugin.dll > SHA256SUMS
)

echo "built Taiko net40 artifacts in: $output_dir"
