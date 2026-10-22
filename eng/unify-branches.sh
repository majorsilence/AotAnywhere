#!/usr/bin/env bash
# Builds (or updates) a branch that merges master and every feature branch, for producing NuGet
# packages of everything in one place, while each feature branch stays its own branch for
# upstreaming. Re-run it whenever master or a feature branch moves: it only merges what's new.
#
#   unify-branches.sh [--update-features] [--push]
#
#   --update-features  first merge origin/master into each feature branch (keeps them current
#                      for upstream PRs); skip it to leave the feature branches untouched
#   --push             push the unified branch (and, with --update-features, the feature branches)
#
# Conflicts: the script stops and leaves the merge in progress. Resolve the files, `git add` them,
# `git commit --no-edit`, and run the script again. rerere is switched on for this repository, so
# a resolution is recorded and replayed automatically the next time the same conflict comes up.
set -euo pipefail

UNIFIED=${UNIFIED:-unified}
FEATURES=(${FEATURES:-ios-support macos-demote-runtime-exports windows-hardening})
REMOTE=${REMOTE:-origin}
update_features=false push=false
script_args="$*"
for a in "$@"; do
  case "$a" in
    --update-features) update_features=true ;;
    --push) push=true ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

cd "$(git rev-parse --show-toplevel)"
if [ -n "$(git status --porcelain --untracked-files=no)" ] || [ -e "$(git rev-parse --git-dir)/MERGE_HEAD" ]; then
  echo "The working tree has uncommitted changes or a merge in progress; commit or finish it first." >&2
  exit 1
fi
git config rerere.enabled true
git config rerere.autoupdate true
git fetch "$REMOTE"
start=$(git symbolic-ref --quiet --short HEAD || git rev-parse HEAD)

# Merges $2 into the checked-out branch; on a conflict rerere can't settle, stops for the user.
merge() {
  if git merge-base --is-ancestor "$2" HEAD; then echo "  $2: already merged"; return; fi
  if git merge --no-ff --no-edit -m "Merge $2 into $1" "$2"; then return; fi
  if [ -z "$(git diff --name-only --diff-filter=U)" ]; then
    git commit --no-edit   # rerere resolved every conflict from an earlier resolution
    return
  fi
  echo
  echo "Conflict merging $2 into $1. Resolve these, then: git add <files> && git commit --no-edit && $0 $script_args"
  git diff --name-only --diff-filter=U | sed 's/^/  /'
  exit 1
}

checkout() {  # local branch $1, created from or fast-forwarded to $REMOTE/$1 where it exists
  if git show-ref --verify --quiet "refs/heads/$1"; then
    git checkout --quiet "$1"
    if git show-ref --verify --quiet "refs/remotes/$REMOTE/$1"; then git merge --ff-only --quiet "$REMOTE/$1"; fi
  elif git show-ref --verify --quiet "refs/remotes/$REMOTE/$1"; then
    git checkout --quiet -b "$1" "$REMOTE/$1"
  else
    git checkout --quiet -b "$1" "$REMOTE/master"
  fi
}

if $update_features; then
  for b in "${FEATURES[@]}"; do
    echo "== $b <- $REMOTE/master"
    checkout "$b"
    merge "$b" "$REMOTE/master"
    $push && git push "$REMOTE" "$b"
  done
fi

echo "== $UNIFIED"
checkout "$UNIFIED"
merge "$UNIFIED" "$REMOTE/master"
for b in "${FEATURES[@]}"; do
  # The local branch when it exists (it may be ahead of the remote), else the remote's.
  if git show-ref --verify --quiet "refs/heads/$b"; then ref=$b; else ref=$REMOTE/$b; fi
  merge "$UNIFIED" "$ref"
done
$push && git push -u "$REMOTE" "$UNIFIED"
git checkout --quiet "$start"
echo "$UNIFIED is up to date with master and: ${FEATURES[*]}"
