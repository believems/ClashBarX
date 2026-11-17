#!/usr/bin/env bash
#
# Sync the local `main` with the upstream repository.
#
# Model: `main` = upstream + a small set of backportable fixes. Keeping the
# local delta small is what makes rebasing onto upstream cheap; anything that
# would conflict with upstream belongs on a `fork/*` branch instead.
#
# Usage:
#   Scripts/sync_upstream.sh            # fetch + rebase main onto upstream/main
#   Scripts/sync_upstream.sh --check    # report divergence only, change nothing
#
# Environment:
#   UPSTREAM_REMOTE  remote name for upstream (default: upstream)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UPSTREAM_REMOTE="${UPSTREAM_REMOTE:-upstream}"
MODE="${1:-sync}"
cd "$ROOT"

if ! git remote get-url "$UPSTREAM_REMOTE" >/dev/null 2>&1; then
  echo "Upstream remote '$UPSTREAM_REMOTE' is not configured." >&2
  echo "Run: git remote add $UPSTREAM_REMOTE https://github.com/Sitoi/ClashBar.git" >&2
  exit 1
fi

if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "Working tree has uncommitted changes; commit or stash them first." >&2
  exit 1
fi

current_branch="$(git rev-parse --abbrev-ref HEAD)"
if [ "$current_branch" != "main" ]; then
  echo "Refusing to sync from branch '$current_branch'. Switch to main first." >&2
  exit 1
fi

git fetch "$UPSTREAM_REMOTE" --tags

ahead="$(git rev-list --count "${UPSTREAM_REMOTE}/main..main")"
behind="$(git rev-list --count "main..${UPSTREAM_REMOTE}/main")"

echo "Local main: ${ahead} commit(s) ahead, ${behind} behind ${UPSTREAM_REMOTE}/main."

if [ "$behind" -eq 0 ]; then
  echo "Already up to date with ${UPSTREAM_REMOTE}/main."
  exit 0
fi

if [ "$MODE" = "--check" ]; then
  echo "Divergence detected. Run without --check to rebase onto ${UPSTREAM_REMOTE}/main."
  exit 0
fi

if [ "$ahead" -eq 0 ]; then
  # Fast-forward is preferable here: main holds no local commits.
  git merge --ff-only "${UPSTREAM_REMOTE}/main"
else
  # Local fixes are replayed on top of upstream, keeping history linear and the
  # delta easy to review or drop once upstream ships equivalent fixes.
  echo "Rebasing ${ahead} local commit(s) onto ${UPSTREAM_REMOTE}/main."
  git rebase "${UPSTREAM_REMOTE}/main"
fi

echo "Sync complete. Review, then push with: git push --force-with-lease origin main"
