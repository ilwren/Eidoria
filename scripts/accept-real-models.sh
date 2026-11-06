#!/usr/bin/env bash
# 一条命令做端到端验收（真实权重）：确保模型就绪 → 跑整条语义搜索链路 → 打印报告。
#
#   scripts/accept-real-models.sh                       # 默认：./models + artifacts/real-model-acceptance/report.md
#   scripts/accept-real-models.sh --models /data/models # 指定模型目录
#   scripts/accept-real-models.sh --out ./report.md     # 指定报告路径
#   scripts/accept-real-models.sh --skip-download       # 只跑验收，不联网补模型
#
# 退出码：0 验收通过；1 用法/环境错误；2 模型准备失败；3 验收失败。
set -uo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

MODELS="${MDEXPLORER_MODELS:-$ROOT_DIR/models}"
OUT="$ROOT_DIR/artifacts/real-model-acceptance/report.md"
SKIP_DOWNLOAD=0

while [ $# -gt 0 ]; do
  case "$1" in
    --models)        shift; [ $# -gt 0 ] || { echo "--models 需要一个目录" >&2; exit 1; }; MODELS="$1" ;;
    --models=*)      MODELS="${1#*=}" ;;
    --out)           shift; [ $# -gt 0 ] || { echo "--out 需要一个路径" >&2; exit 1; }; OUT="$1" ;;
    --out=*)         OUT="${1#*=}" ;;
    --skip-download) SKIP_DOWNLOAD=1 ;;
    -h|--help)       sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "未知参数：$1" >&2; exit 1 ;;
  esac
  shift
done

case "$MODELS" in
  /*) ;;
  *)  MODELS="$PWD/$MODELS" ;;
esac
mkdir -p "$(dirname -- "$OUT")"

log()  { printf '%s\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit "${2:-1}"; }

command -v dotnet >/dev/null 2>&1 || die "需要 .NET SDK 10（dotnet 不在 PATH 上）"

# ── 1. 模型就绪 ─────────────────────────────────────────────────────────────
models_ready() { "$SCRIPT_DIR/fetch-ai-models.sh" --dir "$MODELS" --verify >/dev/null 2>&1; }

if models_ready; then
  log "== 模型已就绪且校验通过：$MODELS"
else
  [ "$SKIP_DOWNLOAD" = "1" ] && die "模型不完整：$MODELS（--skip-download 要求它已就绪且校验通过）" 2
  log "== 模型不完整，开始按 huggingface.co → hf-mirror.com → modelscope.cn 顺序补齐"
  # 逐个来源补齐到同一目录（fetch-ai-models.sh 支持断点续传，不会重下完好的文件）。
  "$SCRIPT_DIR/fetch-ai-models.sh" --dir "$MODELS" || log "   huggingface.co 未完成，换镜像"
  if models_ready; then
    log "== 模型就绪（huggingface.co）"
  else
    HF_ENDPOINT=https://hf-mirror.com "$SCRIPT_DIR/fetch-ai-models.sh" --dir "$MODELS" || log "   hf-mirror.com 未完成，换魔搭"
    if models_ready; then
      log "== 模型就绪（hf-mirror.com）"
    else
      # 魔搭只覆盖已核对的模型，走一次性目录再整体替换。
      STAGING="$MODELS.modelscope"
      rm -rf "$STAGING"
      if command -v python3 >/dev/null 2>&1 &&
         python3 "$SCRIPT_DIR/fetch-ai-models-modelscope.py" --dir "$STAGING" &&
         "$SCRIPT_DIR/fetch-ai-models.sh" --dir "$STAGING" --verify; then
        rm -rf "$MODELS"
        mv "$STAGING" "$MODELS"
        log "== 模型就绪（modelscope.cn）"
      else
        rm -rf "$STAGING"
        die "三个来源都没能补齐模型；请检查网络或手动运行 scripts/fetch-ai-models.sh" 2
      fi
    fi
  fi
fi

# ── 2. 跑验收 ───────────────────────────────────────────────────────────────
export MDEXPLORER_RUN_REAL_MODEL_TESTS=1
export MDEXPLORER_MODELS="$MODELS"
export MDEXPLORER_ACCEPTANCE_REPORT="$OUT"

log "== 运行真实权重验收（首次会加载 ONNX Session，CPU 上约数十秒）"
LOG="$(mktemp -t eidoria-acceptance.XXXXXX)"
trap 'rm -f "$LOG"' EXIT
if ! dotnet test "$ROOT_DIR/tests/MdExplorer.Tests" -c Release \
      --filter "FullyQualifiedName~RealModelAcceptanceTests" 2>&1 | tee "$LOG" | sed 's/^/   /'; then
  :
fi
grep -q "Passed!" "$LOG" || die "验收没通过（完整输出见上方）；报告可能未生成" 3

# ── 3. 打印报告 ─────────────────────────────────────────────────────────────
if [ -f "$OUT" ]; then
  log ""
  log "================ 验收报告 ================"
  cat "$OUT"
  log "========================================="
  log "报告已保存：$OUT"
else
  log "验收通过，但没找到报告文件（预期：$OUT）"
fi
