#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: catch/scripts/verify-corpus.sh <Songs-directory> [osu!.exe]" >&2
  exit 2
fi

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
songs_dir="$(realpath "$1")"

dotnet run --project "$root_dir/catch/CatchPlanner" --configuration Release -- self-test
"$root_dir/catch/InProcess/scripts/build-net40.sh"
"$root_dir/catch/artifacts/inprocess/net40/LocalCatchAgent.PlannerTest.exe"
dotnet run \
  --project "$root_dir/catch/RuntimePlannerValidation" \
  --configuration Release -- \
  "$songs_dir"

if [[ $# -eq 2 ]]; then
  osu_path="$(realpath "$2")"
  "$root_dir/catch/artifacts/inprocess/net40/LocalCatchAgent.MetadataProbe.exe" \
    "$(wslpath -w "$osu_path")"
fi

(
  cd "$root_dir/catch/artifacts/inprocess/net40"
  sha256sum -c SHA256SUMS
)

echo "CATCH VERIFICATION: PASS"
