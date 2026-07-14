#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ $# -lt 1 ]]; then
  echo "usage: $0 /mnt/c/path/to/osu/osu!.exe [output-directory]" >&2
  exit 2
fi
assembly="$1"
output="${2:-$root_dir/reverse/decompiled/osu-rebuilt}"
ilspy="${ILSPY:-$HOME/.dotnet/tools/ilspycmd}"

if [[ ! -x "$ilspy" ]]; then
  echo "ilspycmd not found: $ilspy" >&2
  echo "Install the pinned version with: dotnet tool install --global ilspycmd --version 9.1.0.7988" >&2
  exit 1
fi

if [[ ! -f "$assembly" ]]; then
  echo "assembly not found: $assembly" >&2
  exit 1
fi

if [[ -e "$output" ]]; then
  echo "output already exists; refusing to overwrite: $output" >&2
  exit 1
fi

"$ilspy" \
  --disable-updatecheck \
  --nested-directories \
  --project \
  --outputdir "$output" \
  --referencepath "$(dirname "$assembly")" \
  "$assembly"

echo "decompiled files: $(find "$output" -type f | wc -l)"
