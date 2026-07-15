#!/usr/bin/env bash
set -euo pipefail

if (($# != 1)); then
    echo "usage: $0 /path/to/osu!.exe" >&2
    exit 2
fi

target=$(realpath "$1")
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
output_dir=$(realpath -m "$script_dir/../decompiled/osu-1.3.3.8")
ilspy=${ILSPYCMD:-"$HOME/.dotnet/tools/ilspycmd"}

if [[ ! -x "$ilspy" ]]; then
    echo "ilspycmd was not found at $ilspy" >&2
    echo "install it with: dotnet tool install --global ilspycmd" >&2
    exit 1
fi

actual=$(sha256sum "$target" | awk '{print $1}')
expected=6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d
if [[ "$actual" != "$expected" ]]; then
    echo "unsupported osu!.exe sha256=$actual" >&2
    exit 1
fi

mkdir -p "$output_dir"
"$ilspy" --disable-updatecheck --project --nested-directories \
    --referencepath "$(dirname -- "$target")" \
    --outputdir "$output_dir" \
    "$target"

echo "decompiled project: $output_dir"
