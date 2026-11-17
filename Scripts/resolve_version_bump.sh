#!/usr/bin/env bash
#
# Derive a semantic version bump from conventional-commit message lines.
#
# Reads commit lines from stdin ("%s%n%b" output) and prints one of
# major / minor / patch. Exits 1 when nothing is releasable.
#
# Repo convention prefixes subjects with an emoji (e.g. "✨ feat: ..."), so the
# leading symbol run is stripped before matching. Extracted from
# .github/workflows/release-dmg.yml so the workflow and its tests share one
# implementation.

set -euo pipefail

strip_emoji_prefix() {
  sed -E 's/^([^[:alnum:]]+[[:space:]]*)+//'
}

commit_lines="$(cat | strip_emoji_prefix)"

if grep -Eq 'BREAKING CHANGE|^[^[:space:]]+(\([^)]+\))?!:' <<< "$commit_lines"; then
  echo "major"
elif grep -Eq '^feat(\([^)]+\))?:' <<< "$commit_lines"; then
  echo "minor"
elif grep -Eq '^(fix|perf|refactor|build|ci|revert|chore|docs|style|test)(\([^)]+\))?:' <<< "$commit_lines"; then
  echo "patch"
else
  exit 1
fi
