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
loader_source="$(wslpath -w "$root_dir/InProcess/Loader/LocalManiaAutoDomainManager.cs")"
plugin_source="$(wslpath -w "$root_dir/InProcess/Plugin/Entry.cs")"
live_plan_source="$(wslpath -w "$root_dir/InProcess/Plugin/LivePlanBuilder.cs")"
live_agent_source="$(wslpath -w "$root_dir/InProcess/Plugin/LiveAgent.cs")"
agent_settings_source="$(wslpath -w "$root_dir/InProcess/Plugin/AgentSettings.cs")"
humanizer_source="$(wslpath -w "$root_dir/InProcess/Plugin/Humanizer.cs")"
agent_overlay_source="$(wslpath -w "$root_dir/InProcess/Plugin/AgentOverlay.cs")"
frame_builder_source="$(wslpath -w "$root_dir/InProcess/Plugin/NativeFrameBuilder.cs")"
host_source="$(wslpath -w "$root_dir/InProcess/TestHost/Program.cs")"
frame_test_source="$(wslpath -w "$root_dir/InProcess/TestHost/FrameBuilderProgram.cs")"
live_plan_test_source="$(wslpath -w "$root_dir/InProcess/TestHost/LivePlanProgram.cs")"
metadata_probe_source="$(wslpath -w "$root_dir/InProcess/TestHost/MetadataProbeProgram.cs")"
humanizer_test_source="$(wslpath -w "$root_dir/InProcess/TestHost/HumanizerProgram.cs")"

"$csc" /nologo /target:library /platform:anycpu /optimize+ \
  "/out:$win_output\\LocalManiaAuto.Loader.dll" "$loader_source"
"$csc" /nologo /target:library /platform:anycpu /optimize+ \
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll \
  "/out:$win_output\\LocalManiaAuto.Plugin.dll" \
  "$plugin_source" "$live_plan_source" "$live_agent_source" \
  "$agent_settings_source" "$humanizer_source" "$agent_overlay_source"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalManiaAuto.TestHost.exe" "$host_source"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalManiaAuto.FrameBuilderTest.exe" \
  "$frame_test_source" "$frame_builder_source"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalManiaAuto.LivePlanTest.exe" \
  "$live_plan_test_source" "$live_plan_source"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalManiaAuto.MetadataProbe.exe" \
  "$metadata_probe_source"
"$csc" /nologo /target:exe /platform:x86 /optimize+ \
  "/out:$win_output\\LocalManiaAuto.HumanizerTest.exe" \
  "$humanizer_test_source" "$live_plan_source" "$agent_settings_source" "$humanizer_source"

echo "built net40 artifacts in: $output_dir"
