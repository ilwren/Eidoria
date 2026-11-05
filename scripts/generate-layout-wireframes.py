#!/usr/bin/env python3
"""生成 MdExplorer 响应式布局线框图（SVG）。

每个断点一张：Compact / Medium / Expanded / Large / ExtraLarge。
线框图表达的是「区域、尺寸、层级」而非最终像素稿；颜色取 MD3 浅色
基准色（seed #006A6A 青色系的近似 tonal 值），区域编号与
docs/RESPONSIVE-LAYOUT.md 中的图例一一对应。

用法: python3 scripts/generate-layout-wireframes.py
输出: docs/assets/layouts/layout-*.svg
"""
from pathlib import Path

OUT = Path("docs/assets/layouts")

# --- MD3 近似 tonal 颜色（浅色主题） ---
SURFACE = "#F4FBFA"
SURFACE_CONTAINER = "#E9EFEE"
SURFACE_CONTAINER_HIGH = "#DEE7E6"
PRIMARY = "#006A6A"
PRIMARY_CONTAINER = "#9EF1F0"
ON_PRIMARY = "#FFFFFF"
ON_SURFACE = "#161D1D"
ON_SURFACE_VARIANT = "#3F4948"
OUTLINE = "#6F7A79"
SECONDARY_CONTAINER = "#CCE8E7"
ERROR_CONTAINER = "#FFDAD6"
FOLDER_FILL = "#FFD88A"
BADGE = "#B3261E"

FONT = "font-family=\"'Noto Sans SC','Noto Sans','Microsoft YaHei','PingFang SC',sans-serif\""


def esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


class Svg:
    def __init__(self, w: int, h: int, title: str):
        self.w, self.h = w, h
        self.parts = [
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" {FONT}>',
            f"<title>{esc(title)}</title>",
            f'<rect x="0" y="0" width="{w}" height="{h}" fill="{SURFACE}"/>',
        ]

    def rect(self, x, y, w, h, fill, rx=0, stroke=None, sw=1, dash=None):
        s = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
        d = f' stroke-dasharray="{dash}"' if dash else ""
        self.parts.append(
            f'<rect x="{x:.0f}" y="{y:.0f}" width="{w:.0f}" height="{h:.0f}" rx="{rx}" fill="{fill}"{s}{d}/>'
        )

    def circle(self, cx, cy, r, fill):
        self.parts.append(f'<circle cx="{cx:.0f}" cy="{cy:.0f}" r="{r}" fill="{fill}"/>')

    def text(self, x, y, s, size=13, fill=ON_SURFACE, anchor="start", weight="normal"):
        self.parts.append(
            f'<text x="{x:.0f}" y="{y:.0f}" font-size="{size}" fill="{fill}" '
            f'text-anchor="{anchor}" font-weight="{weight}">{esc(s)}</text>'
        )

    def badge(self, cx, cy, n):
        self.circle(cx, cy, 11, BADGE)
        self.text(cx, cy + 4, str(n), size=11, fill="#fff", anchor="middle", weight="bold")

    def region_label(self, x, y, s, size=11):
        self.text(x, y, s, size=size, fill=ON_SURFACE_VARIANT)

    def save(self, name: str):
        self.parts.append("</svg>")
        OUT.mkdir(parents=True, exist_ok=True)
        p = OUT / name
        p.write_text("\n".join(self.parts), encoding="utf-8")
        print(f"wrote {p} ({self.w}x{self.h})")


def folder_icon(s: Svg, x, y, size=22):
    s.rect(x, y + size * 0.18, size, size * 0.72, FOLDER_FILL, rx=3, stroke=OUTLINE, sw=0.7)
    s.rect(x, y, size * 0.45, size * 0.28, FOLDER_FILL, rx=3, stroke=OUTLINE, sw=0.7)


def file_icon(s: Svg, x, y, size=20, fill="#FFFFFF"):
    s.rect(x + 2, y, size - 4, size, fill, rx=2, stroke=OUTLINE, sw=0.7)


def file_rows(s: Svg, x, y, w, rows, row_h=56, top_pad=8):
    items = [
        ("folder", "Documents", "12 个项目 · 10月20日"),
        ("folder", "Pictures", "248 个项目 · 10月18日"),
        ("image", "IMG_20261001.jpg", "3.2 MB · 10月1日"),
        ("pdf", "设计规范 v3.pdf", "8.7 MB · 9月28日"),
        ("code", "Program.cs", "12 KB · 9月27日"),
        ("md", "README.md", "4 KB · 9月26日"),
        ("zip", "backup-2026.zip", "1.4 GB · 9月20日"),
        ("audio", "录音 001.m4a", "22 MB · 9月19日"),
        ("video", "演示视频.mp4", "380 MB · 9月15日"),
        ("text", "会议纪要.txt", "18 KB · 9月12日"),
        ("folder", "Downloads", "36 个项目 · 9月10日"),
        ("image", "screenshot.png", "1.1 MB · 9月8日"),
        ("pdf", "发票.pdf", "240 KB · 9月5日"),
        ("text", "todo.txt", "2 KB · 9月1日"),
    ]
    for i in range(min(rows, len(items))):
        kind, name, meta = items[i]
        ry = y + top_pad + i * row_h
        if kind == "folder":
            folder_icon(s, x + 14, ry + 12, 26)
        elif kind == "image":
            s.rect(x + 14, ry + 12, 26, 26, PRIMARY_CONTAINER, rx=4, stroke=OUTLINE, sw=0.7)
            s.circle(x + 21, ry + 20, 3, PRIMARY)
        elif kind == "pdf":
            file_icon(s, x + 16, ry + 12, 24, "#FDEDEC")
            s.text(x + 28, ry + 29, "PDF", size=7, fill=BADGE, anchor="middle", weight="bold")
        else:
            file_icon(s, x + 16, ry + 12, 24)
        s.text(x + 56, ry + 26, name, size=14)
        s.text(x + 56, ry + 43, meta, size=11, fill=ON_SURFACE_VARIANT)
        s.text(x + w - 16, ry + 32, "⋮", size=16, fill=ON_SURFACE_VARIANT, anchor="middle")


def file_grid(s: Svg, x, y, w, cols, count=12, cell_h=118, thumb_h=64, gap=8):
    items = ["Documents", "Pictures", "IMG_0101.jpg", "规范.pdf", "Program.cs",
             "README.md", "backup.zip", "录音.m4a", "视频.mp4", "纪要.txt",
             "Downloads", "截图.png", "发票.pdf", "todo.txt", "数据.csv", "演示.pptx"]
    cell_w = (w - gap * (cols + 1)) / cols
    for i in range(min(count, len(items))):
        r, c = divmod(i, cols)
        cx = x + gap + c * (cell_w + gap)
        cy = y + gap + r * (cell_h + gap)
        s.rect(cx, cy, cell_w, thumb_h, SURFACE_CONTAINER, rx=12, stroke=OUTLINE, sw=0.6)
        if i % 5 == 0:
            folder_icon(s, cx + cell_w / 2 - 14, cy + thumb_h / 2 - 12, 28)
        elif i % 3 == 0:
            s.circle(cx + cell_w / 2 - 8, cy + thumb_h / 2 - 6, 4, PRIMARY)
            s.rect(cx + cell_w / 2 - 14, cy + thumb_h / 2 + 4, 28, 4, PRIMARY_CONTAINER, rx=2)
        else:
            file_icon(s, cx + cell_w / 2 - 11, cy + thumb_h / 2 - 12, 22)
        name = items[i]
        s.text(cx + cell_w / 2, cy + thumb_h + 18, name, size=11, anchor="middle")
        s.text(cx + cell_w / 2, cy + thumb_h + 33, "3.2 MB", size=9,
               fill=ON_SURFACE_VARIANT, anchor="middle")


def chip_row(s: Svg, x, y, labels, max_w):
    cx = x
    for lab in labels:
        w = 30 + len(lab) * 13
        if cx + w > x + max_w:
            break
        s.rect(cx, y, w, 32, SURFACE, rx=8, stroke=OUTLINE, sw=1)
        s.text(cx + w / 2, y + 21, lab, size=12, fill=ON_SURFACE, anchor="middle")
        cx += w + 8


def search_bar(s: Svg, x, y, w, hint="搜索文件"):
    s.rect(x, y, w, 52, SURFACE_CONTAINER_HIGH, rx=26)
    s.text(x + 18, y + 33, "⌕", size=18, fill=ON_SURFACE_VARIANT)
    s.text(x + 48, y + 32, hint, size=14, fill=ON_SURFACE_VARIANT)
    s.circle(x + w - 28, y + 26, 15, PRIMARY)
    s.text(x + w - 28, y + 31, "M", size=12, fill=ON_PRIMARY, anchor="middle", weight="bold")


def fab(s: Svg, cx, cy, label="+ 新建", extended=False):
    if extended:
        s.rect(cx - 52, cy - 26, 104, 52, PRIMARY_CONTAINER, rx=16, stroke=OUTLINE, sw=0.6)
        s.text(cx, cy + 5, label, size=14, fill=ON_SURFACE, anchor="middle", weight="bold")
    else:
        s.rect(cx - 28, cy - 28, 56, 56, PRIMARY_CONTAINER, rx=16, stroke=OUTLINE, sw=0.6)
        s.text(cx, cy + 8, "+", size=24, fill=ON_SURFACE, anchor="middle", weight="bold")


def rail(s: Svg, x, y, w, h, labels, active=0):
    s.rect(x, y, w, h, SURFACE_CONTAINER)
    iy = y + 88
    for i, lab in enumerate(labels):
        if i == active:
            s.rect(x + w / 2 - 28, iy - 16, 56, 32, SECONDARY_CONTAINER, rx=16)
        s.circle(x + w / 2, iy, 5, PRIMARY if i == active else ON_SURFACE_VARIANT)
        s.text(x + w / 2, iy + 30, lab, size=11, anchor="middle",
               weight="bold" if i == active else "normal")
        iy += 64


def drawer(s: Svg, x, y, w, h, title="MdExplorer"):
    s.rect(x, y, w, h, SURFACE_CONTAINER)
    s.text(x + 20, y + 38, title, size=17, weight="bold")
    fab(s, x + 76, y + 88, "+ 新建", extended=True)
    entries = ["⌂  主页", "★  收藏", "↺  最近使用", "▣  图片", "🎬  视频", "🎵  音频", "📄  文档"]
    volumes = ["💽  本地磁盘 (C:)  68%", "💽  数据盘 (D:)  31%", "▤  SD 卡  12%"]
    iy = y + 148
    for i, e in enumerate(entries):
        if i == 0:
            s.rect(x + 12, iy - 24, w - 24, 40, SECONDARY_CONTAINER, rx=20)
        s.text(x + 28, iy + 4, e, size=13, weight="bold" if i == 0 else "normal")
        iy += 48
    s.rect(x + 16, iy, w - 32, 1, OUTLINE)
    iy += 30
    s.text(x + 28, iy, "存储设备", size=12, fill=ON_SURFACE_VARIANT, weight="bold")
    iy += 30
    for v in volumes:
        s.text(x + 28, iy, v, size=12)
        iy += 38
    s.rect(x + 16, iy, w - 32, 1, OUTLINE)
    iy += 34
    s.text(x + 28, iy, "🗑  回收站", size=13)
    iy += 40
    s.text(x + 28, iy, "⚙  设置", size=13)


def preview_pane(s: Svg, x, y, w, h):
    s.rect(x, y, w, h, SURFACE_CONTAINER, stroke=OUTLINE, sw=0.8)
    s.text(x + 16, y + 30, "预览", size=14, weight="bold")
    s.text(x + w - 16, y + 30, "✕", size=13, fill=ON_SURFACE_VARIANT, anchor="middle")
    s.rect(x + 16, y + 48, w - 32, 150, PRIMARY_CONTAINER, rx=12, stroke=OUTLINE, sw=0.7)
    s.circle(x + w / 2 - 30, y + 100, 10, PRIMARY)
    s.rect(x + 40, y + 150, w - 80, 10, PRIMARY, rx=5)
    s.text(x + w / 2, y + 185, "图片缩略图 / PDF 首页 / 文本片段", size=10,
           fill=ON_SURFACE_VARIANT, anchor="middle")
    s.text(x + 16, y + 232, "IMG_20261001.jpg", size=15, weight="bold")
    rows = [("类型", "JPEG 图像"), ("大小", "3.2 MB"), ("尺寸", "4032 × 3024"),
            ("修改时间", "2026-10-01 14:22"), ("位置", "Pictures/Camera")]
    ry = y + 264
    for k, v in rows:
        s.text(x + 16, ry, k, size=11, fill=ON_SURFACE_VARIANT)
        s.text(x + w - 16, ry, v, size=11, anchor="end")
        ry += 26
    bw = (w - 48) / 3
    for i, lab in enumerate(["打开", "共享", "更多"]):
        bx = x + 16 + i * (bw + 8)
        fill = PRIMARY if i == 0 else SURFACE
        s.rect(bx, ry + 6, bw, 38, fill, rx=19, stroke=OUTLINE, sw=0.7)
        s.text(bx + bw / 2, ry + 30, lab, size=12,
               fill=ON_PRIMARY if i == 0 else ON_SURFACE, anchor="middle",
               weight="bold")


def header(s: Svg, title, subtitle):
    s.rect(0, 0, s.w, 0, SURFACE)  # no-op keep
    # 画布角标题（设计稿标注，非 App UI）
    s.text(s.w - 12, 22, title, size=12, fill=ON_SURFACE_VARIANT, anchor="end")
    s.text(s.w - 12, 40, subtitle, size=10, fill=OUTLINE, anchor="end")


# ---------------------------------------------------------------- Compact
def compact():
    W, H = 390, 844
    s = Svg(W, H, "MdExplorer Compact <600dp 手机竖屏")
    # 状态栏示意
    s.rect(0, 0, W, 36, SURFACE)
    s.text(16, 24, "9:41", size=12, weight="bold")
    s.text(W - 16, 24, "5G  ▮", size=11, anchor="end", fill=ON_SURFACE_VARIANT)
    # 1 TopAppBar (Small) + 2 SearchBar
    s.rect(0, 36, W, 56, SURFACE)
    s.text(52, 73, "文件", size=20, weight="bold")
    s.text(24, 73, "☰", size=17, anchor="middle")
    s.text(W - 24, 73, "⋮", size=17, anchor="middle")
    search_bar(s, 16, 100, W - 32)
    # 存储卡片
    s.rect(16, 166, W - 32, 74, SURFACE_CONTAINER, rx=16)
    s.text(32, 192, "内部存储", size=14, weight="bold")
    s.rect(32, 202, W - 64, 8, SURFACE_CONTAINER_HIGH, rx=4)
    s.rect(32, 202, (W - 64) * 0.68, 8, PRIMARY, rx=4)
    s.text(32, 230, "已用 87 GB / 128 GB", size=11, fill=ON_SURFACE_VARIANT)
    # 分类 chips
    s.text(16, 272, "类别", size=14, weight="bold")
    chip_row(s, 16, 284, ["图片", "视频", "音频", "文档", "下载", "压缩包"], W - 32)
    # 面包屑压成单行路径 + 排序/视图切换
    s.rect(0, 332, W, 44, SURFACE)
    s.text(16, 360, "内部存储 › Pictures", size=13, weight="bold")
    s.text(W - 58, 360, "⇅", size=15, anchor="middle", fill=ON_SURFACE_VARIANT)
    s.text(W - 26, 360, "▦", size=15, anchor="middle", fill=ON_SURFACE_VARIANT)
    # 3 文件列表（两行信息，触控行高 56dp+）
    file_rows(s, 0, 376, W, 6, row_h=60)
    # 4 FAB
    fab(s, W - 52, H - 128)
    # 5 底部导航栏
    s.rect(0, H - 80, W, 80, SURFACE_CONTAINER)
    nav = [("⌂", "主页"), ("↺", "最近"), ("★", "收藏"), ("⚙", "设置")]
    for i, (g, lab) in enumerate(nav):
        cx = W * (i * 2 + 1) / 8
        if i == 0:
            s.rect(cx - 28, H - 72, 56, 30, SECONDARY_CONTAINER, rx=15)
        s.text(cx, H - 52, g, size=15, anchor="middle")
        s.text(cx, H - 28, lab, size=11, anchor="middle",
               weight="bold" if i == 0 else "normal")
    s.badge(28, 52, 1); s.badge(28, 112, 2); s.badge(28, 388, 3)
    s.badge(W - 88, H - 128, 4); s.badge(28, H - 66, 5)
    header(s, "", "")
    s.save("layout-compact-390.svg")


# ---------------------------------------------------------------- Medium
def medium():
    W, H = 768, 1024
    s = Svg(W, H, "MdExplorer Medium 600–839dp 平板竖屏/折叠屏")
    rail(s, 0, 0, 80, H, ["主页", "最近", "收藏", "图库", "回收站", "设置"])
    fab(s, 40, 52)
    # 顶栏 + 居中搜索
    s.rect(80, 0, W - 80, 64, SURFACE)
    s.text(104, 42, "文件", size=20, weight="bold")
    s.text(W - 24, 40, "⋮", size=17, anchor="middle")
    search_bar(s, 104, 76, W - 128)
    s.text(104, 162, "内部存储 › Pictures", size=14, weight="bold")
    s.text(W - 24, 162, "排序 ▾   ▦ 网格", size=12, anchor="end", fill=ON_SURFACE_VARIANT)
    chip_row(s, 104, 176, ["全部", "图片", "视频", "音频", "文档", "压缩包"], W - 128)
    # 网格为主（4 列）
    file_grid(s, 96, 216, W - 112, cols=4, count=16, cell_h=124)
    # 任务提示 snackbar 示意
    s.rect(104, H - 64, 340, 48, ON_SURFACE, rx=8)
    s.text(120, H - 34, "已复制 3 个项目", size=13, fill="#fff")
    s.text(424, H - 34, "撤销", size=13, fill=PRIMARY_CONTAINER, anchor="end", weight="bold")
    s.badge(40, 110, 1); s.badge(116, 88, 2); s.badge(116, 228, 3); s.badge(72, 52, 4)
    s.save("layout-medium-768.svg")


# ---------------------------------------------------------------- Expanded
def expanded():
    W, H = 1100, 760
    s = Svg(W, H, "MdExplorer Expanded 840–1199dp 平板横屏/小桌面窗口")
    drawer(s, 0, 0, 272, H)
    # 内容区
    X = 272
    s.rect(X, 0, W - X, 64, SURFACE)
    s.text(X + 24, 40, "‹ › ↑", size=15, fill=ON_SURFACE_VARIANT)
    search_bar(s, X + 120, 8, W - X - 360, hint="在 Pictures 中搜索")
    s.text(W - 24, 40, "⇅  ▦  ⋮", size=14, anchor="end", fill=ON_SURFACE_VARIANT)
    # 面包屑（可点段）
    s.text(X + 24, 96, "内部存储  ›  Pictures  ›  Camera", size=14, weight="bold")
    s.text(W - 24, 96, "248 个项目 · 名称 ↑", size=12, anchor="end", fill=ON_SURFACE_VARIANT)
    chip_row(s, X + 24, 110, ["全部", "图片", "视频", "文档"], 400)
    # 双栏：列表 + 预览（选中时出现，可关）
    list_w = W - X - 320
    file_rows(s, X + 8, 150, list_w - 16, 9, row_h=54)
    preview_pane(s, W - 296, 150, 272, H - 150 - 40)
    # 底部状态/任务条
    s.rect(X, H - 40, W - X, 40, SURFACE_CONTAINER)
    s.text(X + 16, H - 15, "已选 1 项 · Pictures 共 248 项", size=12, fill=ON_SURFACE_VARIANT)
    s.text(W - 16, H - 15, "⧗ 正在复制… 62%", size=12, anchor="end", fill=PRIMARY, weight="bold")
    s.badge(20, 30, 1); s.badge(X + 132, 20, 2); s.badge(X + 24, 84, 3)
    s.badge(X + 24, 162, 4); s.badge(W - 284, 162, 5); s.badge(X + 16, H - 52, 6)
    s.save("layout-expanded-1100.svg")


# ---------------------------------------------------------------- Large
def large():
    W, H = 1440, 900
    s = Svg(W, H, "MdExplorer Large 1200–1599dp 桌面")
    drawer(s, 0, 0, 288, H)
    X = 288
    # 桌面顶栏：窗口标题/标签页 + 地址栏式面包屑 + 搜索
    s.rect(X, 0, W - X, 48, SURFACE_CONTAINER_HIGH)
    for i, t in enumerate(["Pictures  ✕", "Documents  ✕", "＋"]):
        tw = 150 if i < 2 else 44
        tx = X + 12 + i * 158
        s.rect(tx, 8, tw, 34, SURFACE if i == 0 else SURFACE_CONTAINER, rx=9)
        s.text(tx + tw / 2, 30, t, size=12, anchor="middle", weight="bold" if i == 0 else "normal")
    s.text(W - 70, 30, "—  ▢  ✕", size=12, anchor="middle", fill=ON_SURFACE_VARIANT)
    s.rect(X, 48, W - X, 56, SURFACE)
    s.text(X + 20, 84, "‹ › ↑ ⟳", size=15, fill=ON_SURFACE_VARIANT)
    s.rect(X + 110, 58, 560, 38, SURFACE, rx=19, stroke=OUTLINE, sw=1)
    s.text(X + 126, 83, "此电脑 › 数据盘 (D:) › Pictures › Camera", size=13)
    search_bar(s, W - 380, 52, 356, hint="在 Camera 中搜索  (Ctrl+K)")
    # 工具栏（桌面：替代 FAB，常显操作）
    s.rect(X, 104, W - X, 48, SURFACE)
    tools = ["＋ 新建", "✂ 剪切", "⧉ 复制", "📋 粘贴", "✎ 重命名", "🗑 删除", "|", "⇅ 排序", "▦ 视图", "… 更多"]
    tx = X + 16
    for t in tools:
        s.text(tx, 134, t, size=12, fill=ON_SURFACE_VARIANT if t == "|" else ON_SURFACE)
        tx += 34 + len(t) * 12
    # 主区：网格 + 预览
    file_grid(s, X + 8, 160, W - X - 352, cols=6, count=18, cell_h=112, thumb_h=58)
    preview_pane(s, W - 324, 160, 300, H - 160 - 36)
    # 状态栏
    s.rect(X, H - 36, W - X, 36, SURFACE_CONTAINER)
    s.text(X + 16, H - 13, "248 个项目 · 已选 1 项 (3.2 MB)", size=12, fill=ON_SURFACE_VARIANT)
    s.text(W - 16, H - 13, "D: 可用 210 GB  ·  100% ▭", size=12, anchor="end", fill=ON_SURFACE_VARIANT)
    s.badge(20, 30, 1); s.badge(X + 24, 16, 2); s.badge(X + 122, 66, 3)
    s.badge(W - 368, 64, 4); s.badge(X + 24, 116, 5); s.badge(X + 24, 172, 6)
    s.badge(W - 312, 172, 7); s.badge(X + 16, H - 48, 8)
    s.save("layout-large-1440.svg")


# ---------------------------------------------------------------- ExtraLarge
def xlarge():
    W, H = 1920, 1080
    s = Svg(W, H, "MdExplorer ExtraLarge 1600dp+ 大屏/超宽屏（可选双窗格）")
    drawer(s, 0, 0, 300, H)
    X = 300
    s.rect(X, 0, W - X, 48, SURFACE_CONTAINER_HIGH)
    s.text(X + 16, 30, "Pictures  ✕      Documents  ✕      ＋ 新标签", size=13)
    s.text(W - 70, 30, "—  ▢  ✕", size=12, anchor="middle", fill=ON_SURFACE_VARIANT)
    s.rect(X, 48, W - X, 56, SURFACE)
    s.text(X + 20, 84, "‹ › ↑ ⟳", size=15, fill=ON_SURFACE_VARIANT)
    s.rect(X + 110, 58, 680, 38, SURFACE, rx=19, stroke=OUTLINE, sw=1)
    s.text(X + 126, 83, "此电脑 › 数据盘 (D:) › Pictures", size=13)
    search_bar(s, W - 430, 52, 406, hint="搜索  (Ctrl+K)")
    s.rect(X, 104, W - X, 48, SURFACE)
    s.text(X + 16, 134, "＋ 新建    ✂ 剪切    ⧉ 复制    📋 粘贴    ✎ 重命名    🗑 删除      ⫴ 双窗格：开", size=13)
    s.text(W - 16, 134, "⇅ 排序  ·  ☰ 详细信息  ·  ⚙ 列设置", size=12, anchor="end", fill=ON_SURFACE_VARIANT)
    # 左窗格：详细信息表头 + 行
    pane_w = (W - X - 336) // 2
    for p in range(2):
        px = X + p * pane_w
        s.rect(px + 8, 162, pane_w - 16, 36, SURFACE_CONTAINER, rx=8)
        s.text(px + 24, 185, "名称 ↑", size=12, weight="bold")
        s.text(px + pane_w - 220, 185, "修改日期", size=12, fill=ON_SURFACE_VARIANT)
        s.text(px + pane_w - 110, 185, "类型", size=12, fill=ON_SURFACE_VARIANT)
        s.text(px + pane_w - 24, 185, "大小", size=12, anchor="end", fill=ON_SURFACE_VARIANT)
        file_rows(s, px + 8, 200, pane_w - 16, 13, row_h=46, top_pad=0)
        s.text(px + 24, 155, "窗格 A · Pictures" if p == 0 else "窗格 B · Documents", size=12,
               fill=PRIMARY, weight="bold")
        if p == 1:
            s.rect(px + 8, 162, pane_w - 16, H - 162 - 48, "none", rx=8, stroke=OUTLINE, sw=1, dash="6 4")
    preview_pane(s, W - 324, 160, 300, H - 160 - 36)
    s.rect(X, H - 36, W - X, 36, SURFACE_CONTAINER)
    s.text(X + 16, H - 13, "窗格 A 248 项 · 窗格 B 96 项 · F6 切换活动窗格 · 拖拽跨窗格移动", size=12, fill=ON_SURFACE_VARIANT)
    s.badge(20, 30, 1); s.badge(X + 24, 170, 2); s.badge(X + pane_w + 24, 170, 3)
    s.badge(W - 312, 172, 4)
    s.save("layout-xlarge-1920.svg")


if __name__ == "__main__":
    compact(); medium(); expanded(); large(); xlarge()
