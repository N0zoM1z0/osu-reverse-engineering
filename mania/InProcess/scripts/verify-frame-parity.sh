#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ $# -lt 1 ]]; then
  echo "usage: $0 /mnt/c/path/to/osu/Songs" >&2
  exit 2
fi
songs_dir="$1"
parallelism="${PARALLELISM:-4}"
net8_cli="$root_dir/ManiaAuto/bin/Release/net8.0/mania-auto.dll"
artifact_dir="$root_dir/artifacts/inprocess/net40"
frame_script="$root_dir/InProcess/scripts/run-frame-builder.ps1"

for required in "$net8_cli" "$artifact_dir/LocalManiaAuto.FrameBuilderTest.exe" "$frame_script"; do
  if [[ ! -f "$required" ]]; then
    echo "missing build/test artifact: $required" >&2
    exit 1
  fi
done

FRAME_SCRIPT="$(wslpath -w "$frame_script")"
ARTIFACTS="$(wslpath -w "$artifact_dir")"
ROOT_DIR="$root_dir"
export FRAME_SCRIPT ARTIFACTS ROOT_DIR

compare_map() {
  set -o pipefail
  local map="$1" windows_map left_hash right_hash frame_count
  windows_map="$(wslpath -w "$map")"
  left_hash="$(
    dotnet "$ROOT_DIR/ManiaAuto/bin/Release/net8.0/mania-auto.dll" frames "$map" \
      | tail -n +2 | cut -d, -f1,2 | sha256sum | cut -d' ' -f1
  )"
  right_hash="$(
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$FRAME_SCRIPT" \
      -ArtifactDirectory "$ARTIFACTS" -Beatmap "$windows_map" -All \
      | tr -d '\r' | tail -n +2 | sha256sum | cut -d' ' -f1
  )"
  frame_count="$(
    dotnet "$ROOT_DIR/ManiaAuto/bin/Release/net8.0/mania-auto.dll" frames "$map" \
      | tail -n +2 | wc -l
  )"

  if [[ "$left_hash" != "$right_hash" ]]; then
    printf 'FAIL\t%s\n' "$map" >&2
    return 1
  fi
  printf 'PASS\t%5d frames\t%s\n' "$frame_count" "$(basename "$map")"
}
export -f compare_map

map_count="$(rg -l --glob '*.osu' '^\s*Mode\s*:\s*3\s*$' "$songs_dir" | wc -l)"
if [[ "$map_count" -eq 0 ]]; then
  echo "no native Mode:3 beatmaps found under: $songs_dir" >&2
  exit 1
fi

rg -l -0 --glob '*.osu' '^\s*Mode\s*:\s*3\s*$' "$songs_dir" \
  | xargs -0 -n1 -P"$parallelism" bash -c 'compare_map "$1"' _ \
  | sort
echo "FULL FRAME PARITY: PASS ($map_count maps)"
