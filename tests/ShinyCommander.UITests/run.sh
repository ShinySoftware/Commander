#!/usr/bin/env bash
# UI tests for the macOS (AppKit) head, driven through the MAUI DevFlow agent - no keystrokes on your desktop.
# See docs/testing.md.
#
#   tests/ShinyCommander.UITests/run.sh                         build the app, then run everything
#   tests/ShinyCommander.UITests/run.sh --no-build              skip the build (reuses the last one)
#   tests/ShinyCommander.UITests/run.sh -t FileOperationTests   one class           (any name fragment works)
#   tests/ShinyCommander.UITests/run.sh -t F5_copies            one test / matching tests
#   tests/ShinyCommander.UITests/run.sh --filter "<expr>"       any `dotnet test --filter` expression
#   tests/ShinyCommander.UITests/run.sh --fresh                 relaunch the app even if a test app is running
#   tests/ShinyCommander.UITests/run.sh --quit                  quit the app afterwards (default: keep it running)
#   tests/ShinyCommander.UITests/run.sh --screens               screenshot every screen/mode into artifacts/screens
#   tests/ShinyCommander.UITests/run.sh --readme-screenshots    regenerate the README's docs/screenshots
#   tests/ShinyCommander.UITests/run.sh --explicit -t Perf      also run [Fact(Explicit = true)] tests (known-red perf checks)
#
# The fast loop when fixing one thing: the test app stays running between runs and the next run attaches to it, so
#   run.sh -t <TestName>             ~1 min (incremental build, relaunch because the binary changed)
#   run.sh --no-build -t <TestName>  seconds (attaches to the running app)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
BUILD=1
FILTER=""
EXPLICIT=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-build) BUILD=0; shift ;;
        -t|--test) FILTER="FullyQualifiedName~$2"; shift 2 ;;
        --filter) FILTER="$2"; shift 2 ;;
        --fresh) export SC_UITEST_FRESH=1; shift ;;
        --quit) export SC_UITEST_QUIT=1; shift ;;
        --screens) export SC_UITEST_SCREENS=1; FILTER="${FILTER:-FullyQualifiedName~ScreensTests}"; shift ;;
        --readme-screenshots) export SC_README_SCREENSHOTS=1; FILTER="${FILTER:-FullyQualifiedName~ReadmeScreenshotsTests}"; shift ;;
        --explicit) EXPLICIT=1; shift ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

step() { printf '\n\033[1;33m== %s\033[0m\n' "$*"; }

command -v maui >/dev/null || { echo "maui CLI missing: dotnet tool install -g Microsoft.Maui.Cli --prerelease" >&2; exit 1; }

if [[ $BUILD == 1 ]]; then
    step "Build ShinyCommander.MacOS (Debug, into tests/ShinyCommander.UITests/.app-build)"
    # own bin/obj so this never fights a dev build of the app over src/ShinyCommander.MacOS/bin|obj
    dotnet build "$ROOT/src/ShinyCommander.MacOS/ShinyCommander.MacOS.csproj" -c Debug -nologo -v:q \
        -p:OutputPath="$HERE/.app-build/bin/" \
        -p:IntermediateOutputPath="$HERE/.app-build/obj/" \
        -p:ValidateXcodeVersion=false
fi
[[ -x "$HERE/.app-build/bin/ShinyCommander.app/Contents/MacOS/ShinyCommander" ]] || { echo "No app build - run without --no-build." >&2; exit 1; }

step "Run the UI tests${FILTER:+ ($FILTER)}"
ARGS=(test "$HERE/ShinyCommander.UITests.csproj" --nologo --logger "console;verbosity=normal")
[[ -n "$FILTER" ]] && ARGS+=(--filter "$FILTER")
if [[ $EXPLICIT == 1 ]]; then
    dotnet "${ARGS[@]}" -- xUnit.Explicit=on
else
    dotnet "${ARGS[@]}"
fi
