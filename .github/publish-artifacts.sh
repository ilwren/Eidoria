#!/usr/bin/env bash
# 将文件发布到 ci-artifacts 分支（沙箱无法下载 Actions 工件/日志 blob 时的回传通道）。
# 用法: publish-artifacts.sh <subdir> <file...>   需要 env: GH_TOKEN, GITHUB_REPOSITORY
# 抗竞态：多个 job 并行发布不同子目录；推送失败时拉取最新分支、重放本次拷贝、重试。
set -uo pipefail
SUB="${1:?subdir required}"; shift
WORK="$(mktemp -d)"
SRC_DIR="$WORK/src"; mkdir -p "$SRC_DIR"
for f in "$@"; do
  if [ -e "$f" ]; then cp -r "$f" "$SRC_DIR/"; fi
done
if [ -z "$(ls -A "$SRC_DIR" 2>/dev/null)" ]; then echo "nothing to publish"; exit 0; fi
REPO="https://x-access-token:${GH_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"
DEST="$WORK/repo"; mkdir -p "$DEST"; cd "$DEST" || exit 0
git init -b ci-artifacts >/dev/null 2>&1
git remote add origin "$REPO" 2>/dev/null || git remote set-url origin "$REPO"

apply_and_commit() {
  mkdir -p "$DEST/$SUB"
  find "$DEST/$SUB" -mindepth 1 -delete 2>/dev/null || true
  cp -r "$SRC_DIR"/. "$DEST/$SUB/"
  git add -A
  git -c user.name=ci -c user.email=ci@users.noreply.github.com \
      commit --allow-empty -m "ci($SUB): run ${GITHUB_RUN_ID:-local} sha ${GITHUB_SHA:-}" >/dev/null 2>&1
}

for attempt in 1 2 3 4 5 6 7 8; do
  if git ls-remote --exit-code --heads origin ci-artifacts >/dev/null 2>&1; then
    git fetch --depth 1 origin ci-artifacts >/dev/null 2>&1 && git reset --hard FETCH_HEAD >/dev/null 2>&1
  else
    git reset --hard >/dev/null 2>&1 || true
    git clean -fdx >/dev/null 2>&1 || true
    printf '# CI artifacts\n\nAuto-generated screenshots and test logs. Do not edit.\n' > README.md
    git add README.md
    git -c user.name=ci -c user.email=ci@users.noreply.github.com commit -m "init" >/dev/null 2>&1 || true
  fi
  apply_and_commit
  if git push origin ci-artifacts 2>/dev/null; then
    echo "published $SUB (attempt $attempt)"; exit 0
  fi
  echo "push raced, retry $attempt"; sleep $(( (RANDOM % 4) + 2 ))
done
echo "publish gave up after retries"; exit 0
