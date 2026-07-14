#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ $# -lt 1 ]]; then
  echo "usage: $0 /mnt/c/path/to/osu/Songs" >&2
  exit 2
fi
songs_dir="$1"
parallelism="${PARALLELISM:-4}"
artifact_dir="$root_dir/artifacts/inprocess/net40"
runner="$root_dir/InProcess/scripts/run-humanizer-test.ps1"

for required in "$artifact_dir/LocalManiaAuto.HumanizerTest.exe" "$runner"; do
  if [[ ! -f "$required" ]]; then
    echo "missing humanizer test artifact: $required" >&2
    exit 1
  fi
done

RUNNER="$(wslpath -w "$runner")"
ARTIFACTS="$(wslpath -w "$artifact_dir")"
export RUNNER ARTIFACTS

verify_humanized_map() {
  local map="$1"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$RUNNER" \
    -ArtifactDirectory "$ARTIFACTS" -Beatmap "$(wslpath -w "$map")" \
    | tr -d '\r'
}
export -f verify_humanized_map

map_count="$(rg -l --glob '*.osu' '^\s*Mode\s*:\s*3\s*$' "$songs_dir" | wc -l)"
if [[ "$map_count" -eq 0 ]]; then
  echo "no native Mode:3 beatmaps found under: $songs_dir" >&2
  exit 1
fi

rg -l -0 --glob '*.osu' '^\s*Mode\s*:\s*3\s*$' "$songs_dir" \
  | xargs -0 -n1 -P"$parallelism" bash -c 'verify_humanized_map "$1"' _ \
  | sort
echo "FULL HUMANIZER PROFILE VALIDATION: PASS ($map_count maps x 4 styles)"
