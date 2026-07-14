#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ $# -lt 1 ]]; then
  echo "usage: $0 /mnt/c/path/to/osu/Songs" >&2
  exit 2
fi
songs_dir="$1"
parallelism="${PARALLELISM:-4}"
tap_ms="${TAP_MS:-8}"
net8_cli="$root_dir/ManiaAuto/bin/Release/net8.0/mania-auto.dll"
artifact_dir="$root_dir/artifacts/inprocess/net40"
plan_script="$root_dir/InProcess/scripts/run-live-plan.ps1"

for required in "$net8_cli" "$artifact_dir/LocalManiaAuto.LivePlanTest.exe" "$plan_script"; do
  if [[ ! -f "$required" ]]; then
    echo "missing build/test artifact: $required" >&2
    exit 1
  fi
done

PLAN_SCRIPT="$(wslpath -w "$plan_script")"
ARTIFACTS="$(wslpath -w "$artifact_dir")"
ROOT_DIR="$root_dir"
TAP_MS="$tap_ms"
export PLAN_SCRIPT ARTIFACTS ROOT_DIR TAP_MS

compare_event_map() {
  set -o pipefail
  local map="$1" windows_map left_hash right_hash transition_count
  windows_map="$(wslpath -w "$map")"
  left_hash="$(
    dotnet "$ROOT_DIR/ManiaAuto/bin/Release/net8.0/mania-auto.dll" \
      events "$map" --tap-ms "$TAP_MS" \
      | tail -n +2 | sha256sum | cut -d' ' -f1
  )"
  right_hash="$(
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PLAN_SCRIPT" \
      -ArtifactDirectory "$ARTIFACTS" -Beatmap "$windows_map" \
      -TapMilliseconds "$TAP_MS" -All \
      | tr -d '\r' | tail -n +2 | sha256sum | cut -d' ' -f1
  )"
  transition_count="$(
    dotnet "$ROOT_DIR/ManiaAuto/bin/Release/net8.0/mania-auto.dll" \
      events "$map" --tap-ms "$TAP_MS" \
      | tail -n +2 | wc -l
  )"

  if [[ "$left_hash" != "$right_hash" ]]; then
    printf 'FAIL\t%s\n' "$map" >&2
    return 1
  fi
  printf 'PASS\t%5d transitions\t%s\n' "$transition_count" "$(basename "$map")"
}
export -f compare_event_map

map_count="$(rg -l --glob '*.osu' '^\s*Mode\s*:\s*3\s*$' "$songs_dir" | wc -l)"
if [[ "$map_count" -eq 0 ]]; then
  echo "no native Mode:3 beatmaps found under: $songs_dir" >&2
  exit 1
fi

rg -l -0 --glob '*.osu' '^\s*Mode\s*:\s*3\s*$' "$songs_dir" \
  | xargs -0 -n1 -P"$parallelism" bash -c 'compare_event_map "$1"' _ \
  | sort
echo "FULL LIVE-EVENT PARITY: PASS ($map_count maps, tap=${tap_ms}ms)"
