#!/usr/bin/env bash
# What /yolo and /wrap-up run before they commit here. This repo commits straight to main, and the
# Build workflow (.github/workflows/build.yml) is the only other reader of a change, after the push.
#
# The build and test steps mirror that workflow's Restore, Build and Test steps command for command,
# so a commit that passes here is held to what the runner requires of main. Change the two together.
# The workflow's publish and packaging steps run only on a tag and are not gated here.
#
# Deliberately absent: a linter. The repo has no lint or analyzer configuration, and adopting one is
# its own decision with its own baseline, not something to add behind a commit gate.
#
# A pass says the conventions hold, the Release build compiles and the unit tests pass. It says
# nothing about the overlay windows, hotkeys or DPI behaviour, which no test here drives.
#
# Prerequisites are `python3` and `dotnet` on PATH. Their absence fails rather than skips: a check
# that cannot tell "passed" from "never ran" turns an open problem into a closed-looking one.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

status=0

# Buffer each step and print its detail only when it fails; the tally line always shows.
run() {
  local label="$1" out
  shift
  out="$(mktemp)"
  if "$@" >"$out" 2>&1; then
    echo "==> $label — $(tail -n 1 "$out")"
  else
    echo "==> $label — FAILED"
    cat "$out"
    status=1
  fi
  rm -f "$out"
}

# First, so a convention violation is reported before the slower .NET steps run.
run "Conventions" python3 ~/.claude/conventions/check.py .

run "Restore" dotnet restore
run "Build (Release)" dotnet build -c Release --no-restore
run "Test" dotnet test tests/ -c Release --no-restore

exit $status
