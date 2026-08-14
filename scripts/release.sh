#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 || ! $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "usage: scripts/release.sh <major.minor.patch>" >&2
  exit 64
fi

version=$1
tag="v$version"
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
cd "$repo_root"

if [[ $(git branch --show-current) != main ]]; then
  echo "releases must be cut from main" >&2
  exit 1
fi

if [[ -n $(git status --porcelain) ]]; then
  echo "the working tree must be clean before releasing" >&2
  git status --short >&2
  exit 1
fi

project_version=$(sed -n 's:.*<Version>\([^<][^<]*\)</Version>.*:\1:p' Monad/Monad.csproj)

if [[ "$project_version" != "$version" ]]; then
  echo "Monad.csproj is version $project_version, not $version" >&2
  exit 1
fi

git fetch origin main --tags

if ! git merge-base --is-ancestor origin/main HEAD; then
  echo "local main is behind or has diverged from origin/main; sync it first" >&2
  exit 1
fi

if git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null; then
  echo "tag $tag already exists" >&2
  exit 1
fi

dotnet restore Monad.slnx --locked-mode
dotnet format Monad.slnx --no-restore --verify-no-changes
dotnet test Monad.slnx --configuration Release --no-restore

git tag --sign "$tag" --message "Monad $version"
git push --atomic origin main "$tag"

echo "Pushed $tag. Follow the Release workflow at:"
echo "  https://github.com/leduftw/monad/actions/workflows/release.yml"
