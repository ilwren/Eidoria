#!/usr/bin/env bash
# Try the pinned Hugging Face endpoint, its compatible mirror, then ModelScope.
# Intended for real-model CI smoke tests; does not run from the application.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
TARGET_DIR=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --dir)
      shift
      [ "$#" -gt 0 ] || { echo "--dir requires a path" >&2; exit 1; }
      TARGET_DIR="$1"
      ;;
    --dir=*) TARGET_DIR="${1#*=}" ;;
    -h|--help)
      echo "Usage: $0 --dir <model-directory>"
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
  shift
done

[ -n "$TARGET_DIR" ] || { echo "Usage: $0 --dir <model-directory>" >&2; exit 1; }

ATTEMPT_DIR="${TARGET_DIR}.provider-attempt"
[ ! -e "$TARGET_DIR" ] || { echo "Refusing to overwrite existing directory: $TARGET_DIR" >&2; exit 1; }
rm -rf "$ATTEMPT_DIR"
trap 'rm -rf "$ATTEMPT_DIR"' EXIT

report_success() {
  local provider="$1"
  local hashes
  hashes="$(awk '$2 ~ /\/onnx\/model_quantized\.onnx$/ { printf "%s=%s;", $2, $1 }' "$TARGET_DIR/checksums.txt")"
  echo "Selected provider: $provider"
  echo "::notice title=Real model provider::Downloaded from $provider"
  echo "::notice title=Real model ONNX SHA-256::$hashes"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
      echo "### Real-model download"
      echo "- Provider: \`$provider\`"
      echo "- Models: \`text-embed-zh\`, \`text-embed-en\`"
      echo "- ONNX SHA-256:"
      awk '$2 ~ /\/onnx\/model_quantized\.onnx$/ { printf "  - `%s`  `%s`\n", $2, $1 }' "$TARGET_DIR/checksums.txt"
    } >> "$GITHUB_STEP_SUMMARY"
  fi
}

try_huggingface() {
  local provider="$1"
  shift
  echo "=== Trying model provider: $provider ==="
  rm -rf "$ATTEMPT_DIR"
  if "$@" --dir "$ATTEMPT_DIR"; then
    if "$SCRIPT_DIR/fetch-ai-models.sh" --dir "$ATTEMPT_DIR" --verify; then
      mv "$ATTEMPT_DIR" "$TARGET_DIR"
      report_success "$provider"
      return 0
    fi
  fi
  echo "Provider failed: $provider" >&2
  return 1
}

if try_huggingface "huggingface.co" "$SCRIPT_DIR/fetch-ai-models.sh"; then
  exit 0
fi

if try_huggingface "hf-mirror.com" env HF_ENDPOINT=https://hf-mirror.com "$SCRIPT_DIR/fetch-ai-models.sh"; then
  exit 0
fi

echo "=== Trying model provider: ModelScope ==="
rm -rf "$ATTEMPT_DIR"
if python3 "$SCRIPT_DIR/fetch-ai-models-modelscope.py" --dir "$ATTEMPT_DIR" &&
   "$SCRIPT_DIR/fetch-ai-models.sh" --dir "$ATTEMPT_DIR" --verify; then
  mv "$ATTEMPT_DIR" "$TARGET_DIR"
  report_success "modelscope.cn"
  exit 0
fi

echo "All model sources failed: huggingface.co, hf-mirror.com, modelscope.cn" >&2
exit 2
