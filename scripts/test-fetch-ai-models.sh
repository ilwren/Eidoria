#!/usr/bin/env bash
# Offline regression test for fetch-ai-models.sh. No model bytes or network access.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/bin"

# Fake curl writes deterministic, model-sized fixtures to the requested .part path.
cat > "$TMP/bin/curl" <<'CURL'
#!/usr/bin/env bash
set -euo pipefail
out=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    *) shift ;;
  esac
done
[ -n "$out" ] || exit 8
head -c 4096 /dev/zero > "$out"
CURL
chmod +x "$TMP/bin/curl"
export PATH="$TMP/bin:$PATH"
DEFAULT_MODELS="$TMP/default-models"
"$ROOT/scripts/fetch-ai-models.sh" --dir "$DEFAULT_MODELS" >/dev/null
[ "$(find "$DEFAULT_MODELS" -type f ! -name checksums.txt | wc -l | tr -d ' ')" = 12 ] || {
  echo 'default download did not contain exactly the two text models' >&2; exit 1;
}
[ ! -d "$DEFAULT_MODELS/audio-asr-tiny" ] && [ ! -d "$DEFAULT_MODELS/audio-classify-yamnet" ] && [ ! -d "$DEFAULT_MODELS/image-vlm-smol" ] || {
  echo 'default download unexpectedly included optional models' >&2; exit 1;
}
"$ROOT/scripts/fetch-ai-models.sh" --dir "$DEFAULT_MODELS" --verify >/dev/null

MODELS="$TMP/models"
# Covers all five packages (including multi-graph VLM/ASR), checksum repair, and scoped clean.
"$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --all >/dev/null
"$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --all --verify >/dev/null

# A known-corrupt file must be replaced, not resumed from or recorded as valid.
printf 'corrupt' > "$MODELS/text-embed-zh/config.json"
"$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --only text-embed-zh >/dev/null
cmp -s <(head -c 4096 /dev/zero) "$MODELS/text-embed-zh/config.json" || {
  echo 'downloader did not replace the corrupted file with the expected fixture' >&2; exit 1;
}
"$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --all --verify >/dev/null

# Cleaning one package must retain checksums for every other installed package.
mkdir -p "$MODELS/text-embed-zh"
printf 'partial' > "$MODELS/text-embed-zh/config.json.part"
"$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --only text-embed-zh --clean >/dev/null
[ ! -e "$MODELS/text-embed-zh/config.json.part" ] || {
  echo 'clean did not remove an interrupted .part file' >&2; exit 1;
}
"$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --only text-embed-en --verify >/dev/null
if "$ROOT/scripts/fetch-ai-models.sh" --dir "$MODELS" --all --verify >/dev/null 2>&1; then
  echo 'verify unexpectedly passed after the zh package was cleaned' >&2
  exit 1
fi

echo 'fetch-ai-models offline regression passed (default/all packages, checksum repair, scoped clean/verify)'
