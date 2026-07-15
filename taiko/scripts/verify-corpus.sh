#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
songs="${1:-${OSU_SONGS:-}}"

if [[ -z "$songs" ]]; then
  echo "usage: $0 /path/to/osu/Songs" >&2
  echo "or set OSU_SONGS" >&2
  exit 2
fi

dotnet run \
  --project "$root_dir/TaikoBeatmap/TaikoBeatmap.csproj" \
  --configuration Release \
  -- corpus "$songs"
