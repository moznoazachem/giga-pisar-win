#!/bin/zsh
# One-command release: build on the Windows machine, write update.json with the
# installer's SHA-256, commit, tag, push, and publish the GitHub release.
#   tools/release.sh "Что нового по-русски" "What's new in English"
set -e -o pipefail
cd "$(dirname "$0")/.."
NOTES_RU="${1:?release notes (ru)}"
NOTES_EN="${2:-$NOTES_RU}"
REPO=moznoazachem/giga-pisar-win
VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' src/GigaPisar.csproj)
[[ -n "$VERSION" ]] || { echo "no <Version> in csproj"; exit 1; }
git diff --quiet || { echo "commit your changes first"; exit 1; }

# The installer is built by GitHub Actions from the tagged source (.github/workflows/build.yml),
# not on a local machine: a draft release waits for it, and update.json gets the hash of the
# very file users will download.
git tag "v$VERSION"
git push -q origin main "v$VERSION"
gh release create "v$VERSION" -R "$REPO" --draft --verify-tag --title "Гига Писарь для Windows $VERSION" --notes "$NOTES_RU"
echo "waiting for the GitHub build…"
sleep 15
RUN=$(gh run list -R "$REPO" --workflow build.yml --branch "v$VERSION" --limit 1 --json databaseId -q '.[0].databaseId')
gh run watch "$RUN" -R "$REPO" --exit-status >/dev/null
mkdir -p dist
rm -f dist/GigaPisar-Setup.exe
gh release download "v$VERSION" -R "$REPO" -p GigaPisar-Setup.exe -D dist
SHA=$(shasum -a 256 dist/GigaPisar-Setup.exe | cut -d' ' -f1)
SIZE=$(stat -f %z dist/GigaPisar-Setup.exe)
URL="https://github.com/$REPO/releases/download/v$VERSION/GigaPisar-Setup.exe"

python3 - "$VERSION" "$URL" "$SHA" "$SIZE" "$NOTES_RU" "$NOTES_EN" <<'PY'
import json, sys
v, url, sha, size, ru, en = sys.argv[1:]
json.dump({"version": v, "url": url, "sha256": sha, "size": int(size), "notes": ru, "notes_en": en},
          open("update.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
echo >> update.json

git add update.json
git -c user.name="Panda" -c user.email="271212341+moznoazachem@users.noreply.github.com" commit -q -m "Update manifest $VERSION"
gh release edit "v$VERSION" -R "$REPO" --draft=false --latest
git push -q origin main
echo "released $VERSION: $URL ($SIZE bytes, sha256 $SHA)"
