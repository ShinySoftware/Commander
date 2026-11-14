#!/usr/bin/env bash
# Puts the website together in one folder: the pages here, plus the README's screenshots and the app icon, which
# live elsewhere in the repo so there's one copy of each.
#
#   website/build.sh <out-dir>     assemble (the Pages workflow uses this)
#   website/build.sh --serve       assemble into a temp folder and serve it on http://localhost:8080
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

assemble() {
    local out="$1"
    rm -rf "$out"
    mkdir -p "$out/screenshots"
    cp "$HERE"/index.html "$HERE"/styles.css "$HERE"/site.js "$out/"
    cp "$ROOT"/docs/screenshots/*.png "$out/screenshots/"
    cp "$ROOT"/src/ShinyCommander.Shared/Resources/AppIcon/appicon.png "$out/appicon.png"
}

if [[ "${1:-}" == "--serve" ]]; then
    out="$(mktemp -d)/site"
    assemble "$out"
    echo "serving $out on http://localhost:8080 (Ctrl+C to stop)"
    cd "$out" && exec python3 -m http.server 8080
fi

assemble "${1:?usage: website/build.sh <out-dir> | --serve}"
echo "website assembled in $1"
