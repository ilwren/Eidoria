#!/usr/bin/env bash
# Eidoria（万象屿）端侧 AI 模型下载脚本（Linux / macOS / WSL）
#
# 想解决的问题：模型体积几十 MB，不能塞进 git；应用又必须在**没有网络**的情况下才能算「端侧」。
# 所以把「取模型」单独做成一个显式动作：脚本按 scripts/ai-models.manifest 下载到应用数据目录，
# 首次下载后把 SHA-256 记进 checksums.txt，之后每次跑都按它校验，坏文件不会静默进内存。
#
# 用法：
#   scripts/fetch-ai-models.sh                    # 下载默认轻量包（约 49 MB）
#   scripts/fetch-ai-models.sh --list             # 只列出模型与许可，不下载
#   scripts/fetch-ai-models.sh --only text-embed-zh
#   scripts/fetch-ai-models.sh --only audio-asr-tiny
#   scripts/fetch-ai-models.sh --only audio-classify-yamnet
#   scripts/fetch-ai-models.sh --only image-vlm-smol
#   scripts/fetch-ai-models.sh --all                # 同时下载可选音频与图像模型（约 316 MB）
#   scripts/fetch-ai-models.sh --dir ./models     # 下载到指定目录（开发用）
#   scripts/fetch-ai-models.sh --verify           # 只校验已下载文件的 SHA-256
#   scripts/fetch-ai-models.sh --dry-run          # 打印将要下载的 URL
#   scripts/fetch-ai-models.sh --clean            # 删掉清单里的模型文件（不动别的文件）
#   HF_ENDPOINT=https://hf-mirror.com scripts/fetch-ai-models.sh   # 换镜像
#
# 退出码：0 全部就绪；1 用法/环境错误；2 下载或校验失败。

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST="${AI_MODELS_MANIFEST:-$SCRIPT_DIR/ai-models.manifest}"

MIRROR="${HF_ENDPOINT:-https://huggingface.co}"
TARGET_DIR=""
ONLY=""
ALL_MODELS=0
DRY_RUN=0
VERIFY_ONLY=0
LIST_ONLY=0
CLEAN=0
FORCE=0

# ── 输出 ────────────────────────────────────────────────────────────────────
log()  { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() { sed -n '2,26p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

# ── 参数 ────────────────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
  case "$1" in
    --list)      LIST_ONLY=1 ;;
    --all)       ALL_MODELS=1 ;;
    --verify)    VERIFY_ONLY=1 ;;
    --dry-run)   DRY_RUN=1 ;;
    --clean)     CLEAN=1 ;;
    --force)     FORCE=1 ;;
    --only)      shift; [ $# -gt 0 ] || die "--only 需要一个模型 id"; ONLY="$1" ;;
    --only=*)    ONLY="${1#*=}" ;;
    --dir)       shift; [ $# -gt 0 ] || die "--dir 需要一个目录"; TARGET_DIR="$1" ;;
    --dir=*)     TARGET_DIR="${1#*=}" ;;
    --mirror)    shift; [ $# -gt 0 ] || die "--mirror 需要一个 URL"; MIRROR="$1" ;;
    --mirror=*)  MIRROR="${1#*=}" ;;
    -h|--help)   usage; exit 0 ;;
    *)           die "不认识的参数：$1（--help 看用法）" ;;
  esac
  shift
done

[ -f "$MANIFEST" ] || die "找不到模型清单：$MANIFEST"

command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 \
  || die "需要 curl 或 wget 才能下载"

sha256_of() {
  local file="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$file" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$file" | awk '{print $1}'
  elif command -v openssl >/dev/null 2>&1; then
    openssl dgst -sha256 "$file" | awk '{print $NF}'
  else
    die "找不到 SHA-256 工具（sha256sum / shasum / openssl 任一即可）"
  fi
}

human() {
  awk -v b="${1:-0}" 'BEGIN {
    split("B KiB MiB GiB", u, " "); i = 1
    while (b >= 1024 && i < 4) { b /= 1024; i++ }
    printf (i == 1 ? "%.0f %s" : "%.1f %s"), b, u[i]
  }'
}

# ── 解析清单（纯文本，bash 无需 JSON 解析器） ───────────────────────────────
MODEL_IDS=()
declare -A M_NAME=() M_PURPOSE=() M_LICENSE=() M_LICENSE_URL=() M_SOURCE=() M_REVISION=() M_RUNTIME=() M_MB=() M_DEFAULT=()
FILE_MODEL=()
FILE_PATH=()

while IFS= read -r raw || [ -n "$raw" ]; do
  line="${raw%%$'\r'}"                      # 容忍 CRLF
  [ -z "${line//[[:space:]]/}" ] && continue
  case "$line" in \#*) continue ;; esac

  kind="${line%%|*}"
  IFS='|' read -r -a parts <<< "$line"
  case "$kind" in
    model)
      [ "${#parts[@]}" -eq 11 ] || die "清单行格式不对（model 需要 11 列）：$line"
      id="${parts[1]}"
      [ -n "${M_NAME[$id]:-}" ] && die "清单里模型 id 重复：$id"
      MODEL_IDS+=("$id")
      M_NAME[$id]="${parts[2]}"
      M_PURPOSE[$id]="${parts[3]}"
      M_LICENSE[$id]="${parts[4]}"
      M_LICENSE_URL[$id]="${parts[5]}"
      M_SOURCE[$id]="${parts[6]}"
      M_REVISION[$id]="${parts[7]}"
      [[ "${M_REVISION[$id]}" =~ ^[0-9a-f]{40}$ ]] || die "模型 revision 必须是 40 位小写十六进制 SHA：$id"
      M_RUNTIME[$id]="${parts[8]}"
      M_MB[$id]="${parts[9]}"
      M_DEFAULT[$id]="${parts[10]}"
      case "${M_DEFAULT[$id]}" in true|false) ;; *) die "清单默认下载字段必须是 true/false：$id" ;; esac
      ;;
    file)
      [ "${#parts[@]}" -eq 3 ] || die "清单行格式不对（file 需要 3 列）：$line"
      [ -n "${M_NAME[${parts[1]}]:-}" ] || die "清单里 file 指向了未知模型：${parts[1]}"
      FILE_MODEL+=("${parts[1]}")
      FILE_PATH+=("${parts[2]}")
      ;;
    *)
      die "清单里有不认识的类型：$kind"
      ;;
  esac
done < "$MANIFEST"

[ "${#MODEL_IDS[@]}" -gt 0 ] || die "清单里没有任何模型"

wanted() {
  local id="$1"
  if [ -z "$ONLY" ]; then
    [ "$ALL_MODELS" -eq 1 ] && return 0
    [ "${M_DEFAULT[$id]:-false}" = "true" ]
    return $?
  fi
  local item
  IFS=',' read -r -a items <<< "$ONLY"
  for item in "${items[@]}"; do
    [ "$item" = "$id" ] && return 0
  done
  return 1
}

files_of() {
  local id="$1" i
  for i in "${!FILE_MODEL[@]}"; do
    [ "${FILE_MODEL[$i]}" = "$id" ] && printf '%s\n' "${FILE_PATH[$i]}"
  done
}

# ── 目录 ────────────────────────────────────────────────────────────────────
if [ -z "$TARGET_DIR" ]; then
  if [ -n "${MDEXPLORER_MODELS:-}" ]; then
    TARGET_DIR="$MDEXPLORER_MODELS"
  elif [ -n "${LOCALAPPDATA:-}" ] && [ "$(uname -s 2>/dev/null || echo)" != "Linux" ]; then
    TARGET_DIR="$LOCALAPPDATA/MdExplorer/models"
  elif [ -n "${LOCALAPPDATA:-}" ] && command -v cmd.exe >/dev/null 2>&1; then
    TARGET_DIR="$LOCALAPPDATA/MdExplorer/models"
  else
    TARGET_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/MdExplorer/models"
  fi
fi
TARGET_DIR="${TARGET_DIR%/}"
CHECKSUMS="$TARGET_DIR/checksums.txt"

# ── --list ──────────────────────────────────────────────────────────────────
if [ "$LIST_ONLY" -eq 1 ]; then
  log "模型清单：$MANIFEST"
  log "下载目录：$TARGET_DIR"
  log ""
  for id in "${MODEL_IDS[@]}"; do
    printf '  %-16s %s\n' "$id" "${M_NAME[$id]}"
    printf '  %-16s 用途：%s\n' "" "${M_PURPOSE[$id]}"
    printf '  %-16s 许可：%s（%s）\n' "" "${M_LICENSE[$id]}" "${M_LICENSE_URL[$id]}"
    printf '  %-16s 仓库：%s · 运行时 %s · 约 %s MB · %s 个文件 · %s\n' "" \
      "${M_SOURCE[$id]}@${M_REVISION[$id]:0:8}" "${M_RUNTIME[$id]}" "${M_MB[$id]}" "$(files_of "$id" | wc -l | tr -d ' ')" \
      "$( [ "${M_DEFAULT[$id]}" = true ] && printf '默认轻量包' || printf '可选，需 --only/--all' )"
    if [ -n "$ONLY" ] && ! wanted "$id"; then
      printf '  %-16s （本次不处理：不在 --only 里）\n' ""
    fi
    log ""
  done
  log "镜像：$MIRROR（用 --mirror 或 HF_ENDPOINT 覆盖）"
  log "无参数时只取默认轻量包；可选音频/图片模型要 --only <id> 或 --all。"
  exit 0
fi

# ── --clean ─────────────────────────────────────────────────────────────────
if [ "$CLEAN" -eq 1 ]; then
  removed=0
  for i in "${!FILE_PATH[@]}"; do
    wanted "${FILE_MODEL[$i]}" || continue
    path="$TARGET_DIR/${FILE_MODEL[$i]}/${FILE_PATH[$i]}"
    if [ -f "$path" ]; then
      rm -f "$path"
      removed=$((removed + 1))
    fi
    # 下载中断留下的临时文件也属于模型，不应被 --clean 遗漏。
    rm -f "$path.part"
    rmdir "$TARGET_DIR/${FILE_MODEL[$i]}/onnx" 2>/dev/null || true
    rmdir "$TARGET_DIR/${FILE_MODEL[$i]}" 2>/dev/null || true
  done
  # 只移除所选模型的校验记录；--clean --only zh 不应让仍安装的 en 模型失去校验和。
  if [ -f "$CHECKSUMS" ]; then
    tmp="$(mktemp)"
    cp "$CHECKSUMS" "$tmp"
    for id in "${MODEL_IDS[@]}"; do
      wanted "$id" || continue
      filtered="$(mktemp)"
      awk -v prefix="$id/" 'index($2, prefix) != 1' "$tmp" > "$filtered"
      mv "$filtered" "$tmp"
    done
    if [ -s "$tmp" ]; then mv "$tmp" "$CHECKSUMS"; else rm -f "$tmp" "$CHECKSUMS"; fi
  fi
  log "已删除 $removed 个模型文件（目录：$TARGET_DIR）"
  exit 0
fi

hash_from_checksums() {
  local rel="$1"
  [ -f "$CHECKSUMS" ] || return 1
  awk -v want="$rel" '$2 == want { print $1; found = 1; exit } END { exit !found }' "$CHECKSUMS"
}

record_checksum() {
  local rel="$1" hash="$2" tmp
  mkdir -p "$TARGET_DIR"
  touch "$CHECKSUMS"
  tmp="$(mktemp)"
  awk -v want="$rel" '$2 != want' "$CHECKSUMS" > "$tmp" || true
  printf '%s  %s\n' "$hash" "$rel" >> "$tmp"
  LC_ALL=C sort -k2 "$tmp" -o "$tmp"
  mv "$tmp" "$CHECKSUMS"
}

# ── --verify ────────────────────────────────────────────────────────────────
if [ "$VERIFY_ONLY" -eq 1 ]; then
  [ -f "$CHECKSUMS" ] || die "$TARGET_DIR 里没有 checksums.txt —— 先正常跑一次下载再校验"
  bad=0
  missing=0
  untracked=0
  for i in "${!FILE_PATH[@]}"; do
    wanted "${FILE_MODEL[$i]}" || continue
    rel="${FILE_MODEL[$i]}/${FILE_PATH[$i]}"
    path="$TARGET_DIR/$rel"
    if [ ! -f "$path" ]; then
      warn "缺少文件：$rel"
      missing=$((missing + 1))
      continue
    fi
    expected="$(hash_from_checksums "$rel" || true)"
    if [ -z "$expected" ]; then
      warn "没有记录校验和：$rel（用一次正常下载补上）"
      untracked=$((untracked + 1))
      continue
    fi
    actual="$(sha256_of "$path")"
    if [ "$actual" != "$expected" ]; then
      warn "校验失败：$rel（期望 ${expected:0:12}…，实际 ${actual:0:12}…）"
      bad=$((bad + 1))
    fi
  done
  if [ "$bad" -gt 0 ] || [ "$missing" -gt 0 ] || [ "$untracked" -gt 0 ]; then
    log "校验未通过：$bad 个损坏、$missing 个缺失、$untracked 个无校验记录（可正常重跑补齐，或加 --force 重新下载）"
    exit 2
  fi
  log "校验通过：清单内文件全部与 checksums.txt 一致"
  exit 0
fi

# ── 下载 ────────────────────────────────────────────────────────────────────
download() {
  local url="$1" partial="$2"
  mkdir -p "$(dirname -- "$partial")"
  if command -v curl >/dev/null 2>&1; then
    # 只对 .part 断点续传；完成且校验通过后才原子改名，半截/错误页永远不会被应用看到。
    curl -sS -fL --retry 3 --retry-delay 2 --connect-timeout 20 -C - -o "$partial" "$url"
  else
    wget -q -c -O "$partial" "$url"
  fi
}

total_ok=0
total_skip=0
total_fail=0

for id in "${MODEL_IDS[@]}"; do
  wanted "$id" || continue
  log "== ${M_NAME[$id]}（${M_LICENSE[$id]}，约 ${M_MB[$id]} MB）"
  log "   仓库 ${M_SOURCE[$id]} · 运行时 ${M_RUNTIME[$id]}"

  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    url="$MIRROR/${M_SOURCE[$id]}/resolve/${M_REVISION[$id]}/$rel"
    dest="$TARGET_DIR/$id/$rel"
    expected="$(hash_from_checksums "$id/$rel" || true)"
    partial="$dest.part"

    if [ -f "$dest" ] && [ "$FORCE" -eq 0 ]; then
      if [ -n "$expected" ]; then
        actual="$(sha256_of "$dest")"
        if [ "$actual" = "$expected" ]; then
          log "   已就绪 $(human "$(wc -c < "$dest" | tr -d ' ')")  $id/$rel"
          total_skip=$((total_skip + 1))
          continue
        fi
        warn "文件与记录的校验和不一致，重新下载：$id/$rel"
        rm -f "$partial"
      else
        log "   已存在（本次记录校验和）  $id/$rel"
        record_checksum "$id/$rel" "$(sha256_of "$dest")"
        total_skip=$((total_skip + 1))
        continue
      fi
    fi

    if [ "$DRY_RUN" -eq 1 ]; then
      log "   [dry-run] $url"
      log "             -> $dest"
      continue
    fi

    # --force 从头下；普通失败则保留 .part 供下次断点续传。
    [ "$FORCE" -eq 0 ] || rm -f "$partial"
    if download "$url" "$partial"; then
      size="$(wc -c < "$partial" | tr -d ' ')"
      if [ "$size" -lt 1024 ]; then
        warn "下载到的文件只有 ${size} 字节，看起来不是模型：$id/$rel"
        rm -f "$partial"
        total_fail=$((total_fail + 1))
        continue
      fi
      actual="$(sha256_of "$partial")"
      if [ -n "$expected" ] && [ "$actual" != "$expected" ]; then
        warn "校验失败（期望 ${expected:0:12}…，实际 ${actual:0:12}…）：$id/$rel"
        rm -f "$partial"
        total_fail=$((total_fail + 1))
        continue
      fi
      mv -f "$partial" "$dest"
      record_checksum "$id/$rel" "$actual"
      log "   下载完成 $(human "$size")  $id/$rel  sha256 ${actual:0:12}…"
      total_ok=$((total_ok + 1))
    else
      warn "下载失败：$url"
      warn "  可换镜像重试：HF_ENDPOINT=https://hf-mirror.com $0 ${ONLY:+--only $ONLY}"
      total_fail=$((total_fail + 1))
    fi
  done < <(files_of "$id")
done

log ""
log "完成：新下载 $total_ok 个、已就绪 $total_skip 个、失败 $total_fail 个"
log "模型目录：$TARGET_DIR"
log "应用会在这里找模型（也可用环境变量 MDEXPLORER_MODELS 指向别处）："
log "  Linux/macOS: \${XDG_DATA_HOME:-\$HOME/.local/share}/MdExplorer/models"
log "  Windows:     %LOCALAPPDATA%\\MdExplorer\\models"
log "校验随时可重跑：$0 --verify（可用 --only 或 --all 选择范围）"

[ "$total_fail" -eq 0 ] || exit 2
