#!/usr/bin/env python3
"""生成 Eidoria（万象屿）v2 高保真概念图（SVG，可选 PNG）。

这些图是设计评审资产，不是应用截图：当前基线图对齐 v0.11，规划图分别
对应搜索索引、双窗格、存储洞察、压缩包工作台、轻编辑/PDF、媒体、插件与
本地 AI。SVG 是可编辑源稿；安装 resvg_py 时会同时渲染 PNG。
中文字体可用环境变量指定：
  EIDORIA_FONT_REGULAR=/path/to/NotoSansCJK-Regular.otf
  EIDORIA_FONT_BOLD=/path/to/NotoSansCJK-Bold.otf
"""
from __future__ import annotations

import math
import os
from dataclasses import dataclass
from pathlib import Path
from xml.sax.saxutils import escape

OUT = Path("docs/assets/mockups/v2")
FONT_STACK = "'Noto Sans SC','Noto Sans CJK SC','Microsoft YaHei','PingFang SC',sans-serif"
MONO_STACK = "'Cascadia Code','JetBrains Mono','DejaVu Sans Mono',monospace"


@dataclass(frozen=True)
class Theme:
    dark: bool
    bg: str
    surface: str
    s1: str
    s2: str
    s3: str
    s4: str
    primary: str
    on_primary: str
    primary_container: str
    on_primary_container: str
    secondary_container: str
    on_secondary_container: str
    text: str
    subtext: str
    faint: str
    outline: str
    inverse: str
    inverse_on: str
    error: str
    error_container: str
    warning: str
    success: str


LIGHT = Theme(
    False, "#DCEFEA", "#FAFCFA", "#F0F5F1", "#E4EEE8", "#D5E5DB", "#C3D9CF",
    "#006B67", "#FFFFFF", "#9CE9DC", "#003B38", "#CDE8E1", "#17423E",
    "#15201D", "#61706A", "#93A39B", "#C4D3CC", "#21302B", "#EAF4EF",
    "#B3261E", "#F9DEDC", "#8A6100", "#18794E",
)
DARK = Theme(
    True, "#07100E", "#0E1714", "#131E1A", "#18241F", "#1F2E28", "#2A3B34",
    "#8ADBC9", "#003731", "#00534D", "#A7F0DF", "#2B4A44", "#C8EAE1",
    "#E7F1EC", "#ACBBB4", "#7E8F87", "#35453E", "#DDEEE8", "#21302B",
    "#FFB4AB", "#5C1D18", "#FFCF6B", "#8BD99B",
)

FILES = [
    ("folder", "Camera", "248 个项目 · 10月18日"),
    ("image", "IMG_20261001.jpg", "3.2 MB · 10月1日"),
    ("image", "海边落日.jpg", "4.1 MB · 9月29日"),
    ("pdf", "设计规范 v3.pdf", "8.7 MB · 9月28日"),
    ("code", "Program.cs", "12 KB · 9月27日"),
    ("md", "README.md", "4 KB · 9月26日"),
    ("archive", "backup-2026.zip", "1.4 GB · 9月20日"),
    ("audio", "录音 001.m4a", "22 MB · 9月19日"),
    ("video", "演示视频.mp4", "380 MB · 9月15日"),
    ("text", "会议纪要.txt", "18 KB · 9月12日"),
    ("folder", "Downloads", "36 个项目 · 9月10日"),
    ("csv", "数据.csv", "86 KB · 9月8日"),
]

TYPE_COLORS = {
    "folder": ("#FFF0C2", "#9A6200", "#F2B93B"),
    "image": ("#D6F0F2", "#096B7A", "#38A7BD"),
    "pdf": ("#FDE5E1", "#B42318", "#E45A4B"),
    "code": ("#DFF3E3", "#176B3A", "#3E9B5F"),
    "md": ("#E7EAF2", "#3E4A6B", "#7180A8"),
    "archive": ("#F3E6D8", "#7A4A21", "#B07A42"),
    "audio": ("#F2E2F4", "#7B2D8B", "#B25EC2"),
    "video": ("#E1E5FF", "#3546A3", "#6676D8"),
    "text": ("#E8ECEA", "#48534F", "#7D8A84"),
    "csv": ("#E2F1DC", "#356B22", "#6AA84F"),
}


class Canvas:
    def __init__(self, w: int, h: int, theme: Theme, title: str):
        self.w, self.h, self.t = w, h, theme
        self.defs: list[str] = [
            '<filter id="softShadow" x="-30%" y="-30%" width="160%" height="160%">'
            '<feGaussianBlur stdDeviation="14"/></filter>'
        ]
        self.parts: list[str] = []
        self.counter = 0
        self.title = title
        self.rect(0, 0, w, h, theme.bg)

    def _id(self, prefix: str) -> str:
        self.counter += 1
        return f"{prefix}{self.counter}"

    def gradient(self, stops, x1=0, y1=0, x2=1, y2=1) -> str:
        gid = self._id("g")
        body = "".join(f'<stop offset="{o}" stop-color="{c}"/>' for o, c in stops)
        self.defs.append(
            f'<linearGradient id="{gid}" x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}">{body}</linearGradient>'
        )
        return f"url(#{gid})"

    def clip_rrect(self, x, y, w, h, r) -> str:
        cid = self._id("clip")
        self.defs.append(
            f'<clipPath id="{cid}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}"/></clipPath>'
        )
        return cid

    def raw(self, s: str) -> None:
        self.parts.append(s)

    def rect(self, x, y, w, h, fill, r=0, stroke=None, sw=1, opacity=1, dash=None):
        st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
        da = f' stroke-dasharray="{dash}"' if dash else ""
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{r}" '
            f'fill="{fill}" opacity="{opacity}"{st}{da}/>'
        )

    def shadow(self, x, y, w, h, r=24, opacity=0.16):
        self.parts.append(
            f'<rect x="{x:.1f}" y="{y+10:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{r}" '
            f'fill="#000000" opacity="{opacity}" filter="url(#softShadow)"/>'
        )

    def card(self, x, y, w, h, fill=None, r=24, stroke=True, shadow=False):
        if shadow:
            self.shadow(x, y, w, h, r, 0.10 if not self.t.dark else 0.28)
        self.rect(x, y, w, h, fill or self.t.surface, r=r,
                  stroke=self.t.outline if stroke else None, sw=1)

    def circle(self, cx, cy, r, fill, stroke=None, sw=1, opacity=1, dash=None, transform=None):
        st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
        da = f' stroke-dasharray="{dash}"' if dash else ""
        tr = f' transform="{transform}"' if transform else ""
        self.parts.append(
            f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{r}" fill="{fill}" opacity="{opacity}"{st}{da}{tr}/>'
        )

    def line(self, x1, y1, x2, y2, stroke, sw=2, opacity=1, dash=None):
        da = f' stroke-dasharray="{dash}"' if dash else ""
        self.parts.append(
            f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
            f'stroke="{stroke}" stroke-width="{sw}" stroke-linecap="round" opacity="{opacity}"{da}/>'
        )

    def path(self, d, fill="none", stroke=None, sw=2, opacity=1, linejoin="round", dash=None):
        st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
        da = f' stroke-dasharray="{dash}"' if dash else ""
        self.parts.append(
            f'<path d="{d}" fill="{fill}" opacity="{opacity}"{st}{da} '
            f'stroke-linecap="round" stroke-linejoin="{linejoin}"/>'
        )

    def text(self, x, y, s, size=14, fill=None, anchor="start", weight=400,
             opacity=1, mono=False, spacing=None):
        ff = MONO_STACK if mono else FONT_STACK
        ls = f' letter-spacing="{spacing}"' if spacing is not None else ""
        self.parts.append(
            f'<text x="{x:.1f}" y="{y:.1f}" font-family="{ff}" font-size="{size}" '
            f'font-weight="{weight}" fill="{fill or self.t.text}" text-anchor="{anchor}" '
            f'opacity="{opacity}"{ls}>{escape(str(s))}</text>'
        )

    def save(self, name: str) -> Path:
        OUT.mkdir(parents=True, exist_ok=True)
        svg = (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{self.w}" height="{self.h}" '
            f'viewBox="0 0 {self.w} {self.h}" font-family="{FONT_STACK}">'
            f"<title>{escape(self.title)}</title><defs>{''.join(self.defs)}</defs>"
            + "".join(self.parts) + "</svg>"
        )
        p = OUT / f"{name}.svg"
        p.write_text(svg, encoding="utf-8")
        print(f"wrote {p} ({self.w}x{self.h})")
        render_png(p, svg)
        return p


def render_png(svg_path: Path, svg: str) -> None:
    try:
        import resvg_py  # type: ignore
    except ImportError:
        return
    fonts = []
    for key in ("EIDORIA_FONT_REGULAR", "EIDORIA_FONT_BOLD"):
        v = os.environ.get(key)
        if v and Path(v).exists():
            fonts.append(v)
    kwargs = {}
    if fonts:
        kwargs["font_files"] = fonts
        kwargs["font_family"] = "Noto Sans CJK SC"
        kwargs["sans_serif_family"] = "Noto Sans CJK SC"
    try:
        data = resvg_py.svg_to_bytes(svg_string=svg, **kwargs)
        out = svg_path.with_suffix(".png")
        out.write_bytes(data)
        print(f"rendered {out} ({len(data)} bytes)")
    except Exception as exc:  # SVG remains the source of truth.
        print(f"PNG render skipped for {svg_path.name}: {exc}")


def icon(c: Canvas, name: str, cx: float, cy: float, s=22, color=None, sw=2):
    col = color or c.t.text
    h = s / 2
    if name == "search":
        c.circle(cx - 2, cy - 2, s * .30, "none", col, sw)
        c.line(cx + s * .15, cy + s * .15, cx + s * .42, cy + s * .42, col, sw)
    elif name == "plus":
        c.line(cx - h, cy, cx + h, cy, col, sw); c.line(cx, cy - h, cx, cy + h, col, sw)
    elif name == "close":
        c.line(cx - h*.7, cy - h*.7, cx + h*.7, cy + h*.7, col, sw)
        c.line(cx + h*.7, cy - h*.7, cx - h*.7, cy + h*.7, col, sw)
    elif name == "check":
        c.path(f"M {cx-h*.8} {cy} L {cx-h*.2} {cy+h*.65} L {cx+h*.85} {cy-h*.65}", stroke=col, sw=sw)
    elif name == "chevR":
        c.path(f"M {cx-h*.35} {cy-h*.7} L {cx+h*.45} {cy} L {cx-h*.35} {cy+h*.7}", stroke=col, sw=sw)
    elif name == "chevD":
        c.path(f"M {cx-h*.7} {cy-h*.35} L {cx} {cy+h*.45} L {cx+h*.7} {cy-h*.35}", stroke=col, sw=sw)
    elif name in ("back", "forward", "up"):
        if name == "back":
            c.path(f"M {cx+h*.45} {cy-h*.7} L {cx-h*.4} {cy} L {cx+h*.45} {cy+h*.7}", stroke=col, sw=sw)
        elif name == "forward":
            icon(c, "chevR", cx, cy, s, col, sw)
        else:
            c.line(cx, cy + h*.8, cx, cy - h*.65, col, sw)
            c.path(f"M {cx-h*.55} {cy-h*.2} L {cx} {cy-h*.75} L {cx+h*.55} {cy-h*.2}", stroke=col, sw=sw)
    elif name == "refresh":
        c.path(f"M {cx+h*.65} {cy-h*.15} A {s*.34} {s*.34} 0 1 1 {cx+h*.2} {cy-h*.62}", stroke=col, sw=sw)
        c.path(f"M {cx+h*.68} {cy-h*.72} L {cx+h*.57} {cy-h*.08} L {cx-h*.02} {cy-h*.34}", stroke=col, sw=sw)
    elif name == "home":
        c.path(f"M {cx-h*.85} {cy+h*.1} L {cx} {cy-h*.85} L {cx+h*.85} {cy+h*.1} M {cx-h*.55} {cy-h*.05} V {cy+h*.8} H {cx+h*.55} V {cy-h*.05}", stroke=col, sw=sw)
    elif name == "star":
        pts=[]
        for i in range(10):
            ang=-math.pi/2+i*math.pi/5; rr=h*(.95 if i%2==0 else .42)
            pts.append(f"{cx+rr*math.cos(ang):.1f},{cy+rr*math.sin(ang):.1f}")
        c.raw(f'<polygon points="{" ".join(pts)}" fill="none" stroke="{col}" stroke-width="{sw}" stroke-linejoin="round"/>')
    elif name == "clock":
        c.circle(cx, cy, h*.82, "none", col, sw)
        c.line(cx, cy, cx, cy-h*.48, col, sw); c.line(cx, cy, cx+h*.38, cy+h*.16, col, sw)
    elif name == "folder":
        c.path(f"M {cx-h*.9} {cy-h*.45} Q {cx-h*.9} {cy-h*.75} {cx-h*.55} {cy-h*.75} H {cx-h*.05} L {cx+h*.25} {cy-h*.45} H {cx+h*.9} V {cy+h*.7} H {cx-h*.9} Z", fill=col, opacity=.92)
    elif name == "file":
        c.path(f"M {cx-h*.55} {cy-h*.9} H {cx+h*.2} L {cx+h*.6} {cy-h*.5} V {cy+h*.9} H {cx-h*.55} Z M {cx+h*.2} {cy-h*.9} V {cy-h*.5} H {cx+h*.6}", stroke=col, sw=sw)
    elif name == "image":
        c.rect(cx-h*.9, cy-h*.75, s*.9, s*.75, "none", r=3, stroke=col, sw=sw)
        c.circle(cx-h*.35, cy-h*.3, 2.6, col)
        c.path(f"M {cx-h*.9} {cy+h*.55} L {cx-h*.15} {cy-h*.05} L {cx+h*.18} {cy+h*.25} L {cx+h*.48} {cy+h*.02} L {cx+h*.9} {cy+h*.55}", stroke=col, sw=sw)
    elif name == "music":
        c.path(f"M {cx+h*.25} {cy+h*.45} V {cy-h*.75} L {cx+h*.85} {cy-h*.55} V {cy+h*.25}", stroke=col, sw=sw)
        c.circle(cx-h*.05, cy+h*.55, s*.20, col); c.circle(cx+h*.58, cy+h*.35, s*.20, col)
    elif name == "video":
        c.rect(cx-h*.95, cy-h*.65, s*.95, s*.68, "none", r=4, stroke=col, sw=sw)
        c.path(f"M {cx-h*.15} {cy-h*.28} L {cx+h*.48} {cy} L {cx-h*.15} {cy+h*.28} Z", fill=col)
    elif name == "archive":
        c.rect(cx-h*.85, cy-h*.75, s*.85, s*.82, "none", r=3, stroke=col, sw=sw)
        c.line(cx-h*.85, cy-h*.35, cx+h*.85, cy-h*.35, col, sw)
        c.line(cx-h*.22, cy+h*.05, cx+h*.22, cy+h*.05, col, sw)
    elif name == "trash":
        c.path(f"M {cx-h*.65} {cy-h*.35} H {cx+h*.65} M {cx-h*.42} {cy-h*.35} L {cx-h*.28} {cy+h*.8} H {cx+h*.28} L {cx+h*.42} {cy-h*.35} M {cx-h*.25} {cy-h*.75} H {cx+h*.25}", stroke=col, sw=sw)
    elif name == "settings":
        pts=[]
        for i in range(16):
            a=i*math.pi/8
            rr=h*(.94 if i%2==0 else .70)
            pts.append(f"{cx+rr*math.cos(a):.1f},{cy+rr*math.sin(a):.1f}")
        c.raw(f'<polygon points="{" ".join(pts)}" fill="{col}" stroke-linejoin="round"/>')
        c.circle(cx, cy, h*.30, c.t.surface)
    elif name == "cut":
        c.circle(cx-h*.42, cy+h*.52, h*.28, "none", col, sw)
        c.circle(cx+h*.42, cy+h*.52, h*.28, "none", col, sw)
        c.line(cx-h*.25, cy+h*.30, cx+h*.65, cy-h*.78, col, sw)
        c.line(cx+h*.25, cy+h*.30, cx-h*.65, cy-h*.78, col, sw)
    elif name == "copy":
        c.rect(cx-h*.62, cy-h*.52, s*.62, s*.72, "none", r=3, stroke=col, sw=sw)
        c.rect(cx-h*.28, cy-h*.82, s*.62, s*.72, "none", r=3, stroke=col, sw=sw)
    elif name == "paste":
        c.rect(cx-h*.62, cy-h*.72, s*.62, s*.82, "none", r=4, stroke=col, sw=sw)
        c.rect(cx-h*.28, cy-h*.94, s*.28, s*.20, col, r=3)
    elif name == "edit":
        c.path(f"M {cx-h*.72} {cy+h*.62} L {cx-h*.58} {cy+h*.08} L {cx+h*.42} {cy-h*.72} L {cx+h*.74} {cy-h*.40} L {cx-h*.24} {cy+h*.48} Z", stroke=col, sw=sw)
        c.line(cx-h*.72, cy+h*.82, cx+h*.72, cy+h*.82, col, sw)
    elif name == "dots":
        for dy in (-h*.55, 0, h*.55): c.circle(cx, cy+dy, 2.2, col)
    elif name == "dotsH":
        for dx in (-h*.55, 0, h*.55): c.circle(cx+dx, cy, 2.2, col)
    elif name == "database":
        c.path(f"M {cx-h*.75} {cy-h*.55} Q {cx} {cy-h*1.05} {cx+h*.75} {cy-h*.55} V {cy+h*.55} Q {cx} {cy+h*1.02} {cx-h*.75} {cy+h*.55} Z M {cx-h*.75} {cy} Q {cx} {cy+h*.48} {cx+h*.75} {cy}", stroke=col, sw=sw)
    elif name == "chart":
        c.line(cx-h*.8, cy+h*.8, cx+h*.85, cy+h*.8, col, sw)
        for i,hh in enumerate((.55,1.0,.75)):
            x=cx-h*.55+i*s*.28; c.line(x,cy+h*.78,x,cy+h*.78-s*hh*.55,col,sw+1)
    elif name == "shield":
        c.path(f"M {cx} {cy-h*.9} L {cx+h*.75} {cy-h*.55} V {cy+h*.05} Q {cx+h*.7} {cy+h*.72} {cx} {cy+h*.95} Q {cx-h*.7} {cy+h*.72} {cx-h*.75} {cy+h*.05} V {cy-h*.55} Z", stroke=col, sw=sw)
    elif name == "spark":
        c.path(f"M {cx} {cy-h} Q {cx+h*.18} {cy-h*.18} {cx+h} {cy} Q {cx+h*.18} {cy+h*.18} {cx} {cy+h} Q {cx-h*.18} {cy+h*.18} {cx-h} {cy} Q {cx-h*.18} {cy-h*.18} {cx} {cy-h} Z", fill=col)
    elif name == "puzzle":
        c.rect(cx-h*.72, cy-h*.72, s*.72, s*.72, "none", r=5, stroke=col, sw=sw)
        c.circle(cx+h*.45, cy-h*.25, s*.17, col); c.circle(cx-h*.25, cy+h*.48, s*.17, col)
    elif name == "play":
        c.path(f"M {cx-h*.45} {cy-h*.7} L {cx+h*.75} {cy} L {cx-h*.45} {cy+h*.7} Z", fill=col)
    elif name == "pause":
        c.line(cx-h*.32, cy-h*.7, cx-h*.32, cy+h*.7, col, sw+1); c.line(cx+h*.32, cy-h*.7, cx+h*.32, cy+h*.7, col, sw+1)
    elif name == "lock":
        c.rect(cx-h*.65, cy-h*.05, s*.65, s*.58, "none", r=4, stroke=col, sw=sw)
        c.path(f"M {cx-h*.38} {cy-h*.05} V {cy-h*.42} Q {cx} {cy-h*.9} {cx+h*.38} {cy-h*.42} V {cy-h*.05}", stroke=col, sw=sw)
    elif name == "sliders":
        for yy in (cy-h*.5, cy, cy+h*.5):
            c.line(cx-h*.85, yy, cx+h*.85, yy, col, sw)
        for xx,yy in ((cx-h*.2,cy-h*.5),(cx+h*.25,cy),(cx-h*.35,cy+h*.5)): c.circle(xx,yy,3.2,col)
    elif name == "grid":
        for dx in (-h*.45,h*.45):
            for dy in (-h*.45,h*.45): c.rect(cx+dx-4,cy+dy-4,8,8,col,r=2)
    elif name == "list":
        for yy in (cy-h*.5,cy,cy+h*.5):
            c.circle(cx-h*.65,yy,2.2,col); c.line(cx-h*.3,yy,cx+h*.8,yy,col,sw)
    else:
        c.text(cx, cy+5, name[:1].upper(), size=s*.72, fill=col, anchor="middle", weight=700)


def app_icon(c: Canvas, x, y, size=48):
    c.rect(x, y, size, size, "#D9F1EB", r=size*.24)
    c.rect(x+size*.26, y+size*.20, size*.56, size*.66, "#67CDBB", r=size*.12)
    c.rect(x+size*.17, y+size*.13, size*.56, size*.66, "#2479E8", r=size*.12)
    c.path(f"M {x+size*.17} {y+size*.38} Q {x+size*.43} {y+size*.36} {x+size*.47} {y+size*.13} L {x+size*.17} {y+size*.13} Z", fill="#20B486")
    c.path(f"M {x+size*.17} {y+size*.38} Q {x+size*.43} {y+size*.36} {x+size*.47} {y+size*.13} Q {x+size*.47} {y+size*.42} {x+size*.38} {y+size*.43} Q {x+size*.25} {y+size*.44} {x+size*.17} {y+size*.38} Z", fill="#FF8A2A")


def button(c: Canvas, x, y, w, h, label, kind="filled", icon_name=None, size=13):
    t=c.t
    if kind=="filled": fill, col, stroke = t.primary, t.on_primary, None
    elif kind=="tonal": fill, col, stroke = t.secondary_container, t.on_secondary_container, None
    elif kind=="outline": fill, col, stroke = "none", t.primary, t.outline
    elif kind=="danger": fill, col, stroke = t.error_container, t.error, None
    else: fill, col, stroke = "none", t.primary, None
    c.rect(x,y,w,h,fill,r=h/2,stroke=stroke)
    tx=x+w/2
    if icon_name:
        icon(c,icon_name,x+24,y+h/2,17,col,2); tx+=10
    c.text(tx,y+h/2+size*.36,label,size=size,fill=col,anchor="middle",weight=650)


def chip(c: Canvas, x, y, w, label, selected=False, h=32, size=12):
    t=c.t
    fill=t.secondary_container if selected else t.surface
    c.rect(x,y,w,h,fill,r=9,stroke=None if selected else t.outline)
    if selected: icon(c,"check",x+17,y+h/2,13,t.on_secondary_container,2)
    c.text(x+w/2+(7 if selected else 0),y+h/2+4,label,size=size,
           fill=t.on_secondary_container if selected else t.text,anchor="middle",weight=600 if selected else 500)


def toggle(c: Canvas, x, y, on=True, scale=1):
    w,h=46*scale,26*scale
    c.rect(x,y,w,h,c.t.primary_container if on else c.t.s3,r=h/2,stroke=c.t.outline if not on else None)
    c.circle(x+(w-h/2 if on else h/2),y+h/2,h*.36,c.t.on_primary_container if on else c.t.subtext)


def progress(c: Canvas, x, y, w, ratio, h=7, color=None):
    c.rect(x,y,w,h,c.t.s3,r=h/2)
    c.rect(x,y,max(h,w*ratio),h,color or c.t.primary,r=h/2)


def file_icon(c: Canvas, kind, x, y, size=42):
    bg, fg, accent = TYPE_COLORS.get(kind, TYPE_COLORS["text"])
    if c.t.dark:
        bg = c.t.s3
    c.rect(x,y,size,size,bg,r=size*.24)
    cx,cy=x+size/2,y+size/2
    if kind=="folder": icon(c,"folder",cx,cy,size*.52,accent)
    elif kind=="image":
        # Mini landscape rather than a generic glyph.
        clip=c.clip_rrect(x+3,y+3,size-6,size-6,size*.18)
        c.raw(f'<g clip-path="url(#{clip})">')
        c.rect(x+3,y+3,size-6,size-6,"#7BC7D9")
        c.circle(x+size*.68,y+size*.34,size*.10,"#FFE19A")
        c.path(f"M {x+3} {y+size*.68} Q {x+size*.35} {y+size*.48} {x+size*.58} {y+size*.65} T {x+size-3} {y+size*.60} V {y+size-3} H {x+3} Z",fill="#14555B")
        c.raw("</g>")
    elif kind=="pdf": c.text(cx,cy+5,"PDF",size=size*.26,fill=fg,anchor="middle",weight=800)
    elif kind=="code": c.text(cx,cy+5,"</>",size=size*.27,fill=fg,anchor="middle",weight=800,mono=True)
    elif kind=="md": c.text(cx,cy+5,"M↓",size=size*.25,fill=fg,anchor="middle",weight=800)
    elif kind=="csv": c.text(cx,cy+5,"{;}",size=size*.25,fill=fg,anchor="middle",weight=800,mono=True)
    else:
        glyph = "music" if kind == "audio" else kind if kind in ("video", "archive") else "file"
        icon(c,glyph,cx,cy,size*.5,fg,2)


def thumbnail(c: Canvas, x, y, w, h, kind="coast", r=16):
    clip=c.clip_rrect(x,y,w,h,r)
    c.raw(f'<g clip-path="url(#{clip})">')
    if kind in ("coast","sunset","mountain","city"):
        palettes={
            "coast":((0,"#8ED8E8"),(.55,"#DFF5E9"),(1,"#FFF3C4")),
            "sunset":((0,"#FFB36B"),(.5,"#FF7E7A"),(1,"#724C9B")),
            "mountain":((0,"#9CC7FF"),(.55,"#D7F0E4"),(1,"#FFF0B8")),
            "city":((0,"#1D3557"),(.6,"#457B9D"),(1,"#F4A261")),
        }
        c.rect(x,y,w,h,c.gradient(palettes[kind],0,0,0,1))
        c.circle(x+w*.72,y+h*.34,min(w,h)*.13,"#FFE49A",opacity=.95)
        if kind=="city":
            for i,bh in enumerate((.42,.58,.36,.66,.48)):
                bx=x+w*(.08+i*.17); c.rect(bx,y+h*(1-bh),w*.12,h*bh,"#132238",r=3)
                for wy in range(3): c.rect(bx+7,y+h*(1-bh)+10+wy*16,5,5,"#FFD166")
        else:
            c.path(f"M {x} {y+h*.67} Q {x+w*.28} {y+h*.48} {x+w*.52} {y+h*.64} T {x+w} {y+h*.56} V {y+h} H {x} Z",fill="#14555B" if kind!="sunset" else "#3D356B")
            c.path(f"M {x} {y+h*.78} Q {x+w*.35} {y+h*.63} {x+w*.63} {y+h*.76} T {x+w} {y+h*.70} V {y+h} H {x} Z",fill="#0E3F46" if kind!="sunset" else "#241F4E",opacity=.82)
    elif kind=="pdf":
        c.rect(x,y,w,h,"#FFFFFF" if not c.t.dark else "#EDF2EF")
        title_size = 13 if h < 120 else 17
        c.text(x+16,y+28,"设计规范",size=title_size,fill="#17201D",weight=800)
        line_top = y + (44 if h < 120 else 52)
        for i in range(3 if h < 120 else 5):
            c.rect(x+16,line_top+i*13,w-32-(24 if i%2 else 0),5,"#CBD5D1",r=3)
        if h >= 150:
            for i,bh in enumerate((34,58,44,72)):
                c.rect(x+24+i*30,y+h-24-bh,16,bh,"#2A9D8F",r=3)
        elif h >= 110:
            for i,bh in enumerate((16,28,22,34)):
                c.rect(x+20+i*24,y+h-14-bh,12,bh,"#2A9D8F",r=3)
    elif kind=="code":
        c.rect(x,y,w,h,"#10201C")
        lines=[("public async Task","#8ADBC9"),("LoadAsync()","#D6EDE7"),("await Refresh();","#FFD166"),("Preview.Open();","#8ADBC9")]
        fs = 10 if w < 180 else 12
        for i,(s,col) in enumerate(lines): c.text(x+14,y+28+i*20,s,size=fs,fill=col,mono=True)
    else:
        c.rect(x,y,w,h,c.t.s2); file_icon(c,kind,x+w/2-25,y+h/2-25,50)
    c.raw("</g>")
    c.rect(x,y,w,h,"none",r=r,stroke=c.t.outline,sw=1,opacity=.75)


def nav_drawer(c: Canvas, x, y, w, h, active="主页", planned_items=()):
    t=c.t
    c.rect(x,y,w,h,t.s1)
    app_icon(c,x+20,y+20,46)
    c.text(x+76,y+40,"Eidoria",size=21,weight=800)
    c.text(x+76,y+61,"万象屿 · 本地文件",size=11,fill=t.subtext)
    button(c,x+20,y+86,104,40,"新建","outline","plus",13)
    entries=[("主页","home"),("收藏","star"),("最近使用","clock"),("图片","image"),("视频","video"),("文档","file")]
    entries += [(n,i) for n,i in planned_items]
    yy=y+148
    for label,ic in entries:
        if label==active: c.rect(x+12,yy-20,w-24,42,t.secondary_container,r=21)
        icon(c,ic,x+38,yy+2,20,t.on_secondary_container if label==active else t.subtext,2)
        c.text(x+58,yy+6,label,size=13,weight=700 if label==active else 500,
               fill=t.on_secondary_container if label==active else t.text)
        yy+=48
    yy+=4; c.line(x+20,yy,x+w-20,yy,t.outline,1); yy+=28
    c.text(x+24,yy,"存储设备",size=11,fill=t.subtext,weight=700); yy+=18
    drives=[("文件系统",44),("数据盘 (D:)",31),("SD 卡",12)]
    for label,pct in drives:
        icon(c,"database",x+36,yy+12,18,t.subtext)
        c.text(x+56,yy+10,label,size=12)
        c.text(x+w-22,yy+10,f"{pct}%",size=11,fill=t.subtext,anchor="end")
        progress(c,x+56,yy+20,w-78,pct/100,5)
        yy+=48
    # Bottom destinations
    by=y+h-92
    for label,ic in (("回收站","trash"),("设置","settings")):
        if label==active: c.rect(x+12,by-20,w-24,42,t.secondary_container,r=21)
        icon(c,ic,x+38,by+2,20,t.subtext); c.text(x+58,by+6,label,size=13,weight=600 if label==active else 500)
        by+=46


def desktop_window(c: Canvas, active="主页", planned_items=()):
    t=c.t
    c.shadow(18,18,c.w-36,c.h-36,28,.12 if not t.dark else .3)
    clip=c.clip_rrect(18,18,c.w-36,c.h-36,28)
    c.raw(f'<g clip-path="url(#{clip})">')
    c.rect(18,18,c.w-36,c.h-36,t.surface)
    nav_drawer(c,18,18,272,c.h-36,active,planned_items)
    c.raw("</g>")
    c.rect(18,18,c.w-36,c.h-36,"none",r=28,stroke=t.outline)


def file_row(c: Canvas, x, y, w, kind, name, meta, selected=False, h=62, trailing=True):
    t=c.t
    if selected: c.rect(x,y,w,h,t.secondary_container,r=16,stroke=t.primary,sw=1.2)
    file_icon(c,kind,x+12,y+(h-42)/2,42)
    c.text(x+66,y+h/2-3,name,size=13,weight=650 if selected else 550)
    c.text(x+66,y+h/2+16,meta,size=11,fill=t.subtext)
    if trailing: icon(c,"dots",x+w-22,y+h/2,18,t.subtext)


def file_card(c: Canvas, x, y, w, h, kind, name, meta, selected=False):
    t=c.t
    c.card(x,y,w,h,t.surface,r=20,stroke=True,shadow=False)
    if selected: c.rect(x,y,w,h,"none",r=20,stroke=t.primary,sw=2)
    thumb_kind = "coast" if kind=="image" else kind
    if kind=="folder":
        c.rect(x+10,y+10,w-20,h-66,t.s1,r=14)
        icon(c,"folder",x+w/2,y+(h-56)/2,54,"#E9A93B")
    else:
        thumbnail(c,x+10,y+10,w-20,h-66,thumb_kind,14)
    c.text(x+14,y+h-28,name,size=12,weight=650)
    c.text(x+14,y+h-10,meta,size=10,fill=t.subtext)
    if selected:
        c.circle(x+w-22,y+22,11,t.primary); icon(c,"check",x+w-22,y+22,12,t.on_primary,2)


def preview_tabs(c: Canvas, x, y, w, selected=0):
    c.text(x+w/4,y+18,"预 览",size=13,anchor="middle",weight=700 if selected==0 else 500,
           fill=c.t.text if selected==0 else c.t.subtext)
    c.text(x+w*3/4,y+18,"属 性",size=13,anchor="middle",weight=700 if selected==1 else 500,
           fill=c.t.text if selected==1 else c.t.subtext)
    c.line(x,y+30,x+w,y+30,c.t.outline,1)
    sx=x if selected==0 else x+w/2
    c.line(sx+6,y+30,sx+w/2-6,y+30,c.t.primary,3)


def section_title(c: Canvas, x, y, title, subtitle=None):
    c.text(x,y,title,size=22,weight=800)
    if subtitle: c.text(x,y+24,subtitle,size=12,fill=c.t.subtext)


def estimate_text_width(s: str, size: float) -> float:
    return sum(size * (0.98 if ord(ch) > 127 else 0.58 if ch != " " else 0.32) for ch in s)


def breadcrumb(c: Canvas, x, y, w, parts, h=42):
    t=c.t
    c.rect(x,y,w,h,t.s1,r=h/2,stroke=t.outline)
    xx=x+18
    for i,p in enumerate(parts):
        weight=700 if i==len(parts)-1 else 500
        c.text(xx,y+h/2+5,p,size=12,weight=weight,fill=t.text if i==len(parts)-1 else t.subtext)
        xx += estimate_text_width(p, 12) + 10
        if i < len(parts)-1:
            icon(c,"chevR",xx-2,y+h/2,13,t.faint,2); xx+=16


def search_island(c: Canvas, x, y, label="搜索", w=104, h=40):
    t=c.t
    c.rect(x,y,w,h,t.inverse,r=h/2)
    if w<=46:
        icon(c,"search",x+w/2,y+h/2,20,t.inverse_on,2)
    else:
        icon(c,"search",x+21,y+h/2,18,t.inverse_on,2)
        c.text(x+38,y+h/2+5,label,size=13,fill=t.inverse_on,weight=650)


def desktop_browser(theme: Theme, name: str):
    c=Canvas(1440,900,theme,f"Eidoria Large 浏览器 · {'深色' if theme.dark else '浅色'}")
    t=theme
    desktop_window(c,"图片")
    c.card(306,18,784,864,t.surface,r=26,shadow=False)
    c.card(1106,18,316,864,t.surface,r=26,shadow=False)
    c.rect(330,30,150,42,t.s2,r=13)
    icon(c,"folder",352,51,18,"#E9A93B"); c.text(368,56,"Pictures",size=12,weight=700)
    icon(c,"close",462,51,11,t.subtext)
    c.line(342,72,468,72,t.primary,3)
    c.rect(490,30,150,42,t.surface,r=13,stroke=t.outline)
    icon(c,"file",512,51,18,t.subtext); c.text(528,56,"Documents",size=12,fill=t.subtext)
    c.circle(662,51,18,t.s1); icon(c,"plus",662,51,16,t.subtext)
    for i,ic in enumerate(("back","forward","up","refresh")):
        cx=344+i*38; c.circle(cx,108,16,t.s1 if i==0 else "none"); icon(c,ic,cx,108,17,t.subtext)
    breadcrumb(c,492,87,374,["此电脑","数据盘","Pictures","Camera"])
    search_island(c,880,88,"搜索",106,40)
    icon(c,"dots",1012,108,20,t.subtext)
    button(c,330,146,126,40,"新建文件夹","tonal","folder",12)
    actions=[("新文件","file"),("剪切","cut"),("复制","copy"),("粘贴","paste"),("重命名","edit"),("删除","trash")]
    xx=466
    for label,ic in actions:
        icon(c,ic,xx+12,166,16,t.subtext); c.text(xx+25,171,label,size=12,fill=t.text)
        xx += 82 if len(label)<3 else 98
    icon(c,"sliders",1018,166,19,t.subtext); icon(c,"grid",1052,166,19,t.primary)
    c.line(330,198,1066,198,t.outline,1)
    subset=[FILES[0],FILES[1],FILES[2],FILES[3],FILES[4],FILES[5],FILES[6],FILES[7],FILES[8],FILES[9],FILES[10],FILES[11]]
    cw,ch,gap=174,178,16
    for i,(kind,nm,meta) in enumerate(subset):
        r,col=divmod(i,4); x=330+col*(cw+gap); y=214+r*(ch+gap)
        file_card(c,x,y,cw,ch,kind,nm,meta.split(" · ")[0],selected=(i==1))
    c.text(330,858,"248 个项目 · 已选 1 项（3.2 MB）",size=11,fill=t.subtext)
    c.text(1066,858,"排序：名称    任务",size=11,fill=t.subtext,anchor="end")
    px=1106
    c.text(px+22,56,"预览",size=16,weight=800); icon(c,"close",px+292,50,16,t.subtext)
    c.text(px+22,92,"IMG_20261001.jpg",size=17,weight=800)
    c.text(px+22,113,"JPG 文件 · 图像",size=11,fill=t.subtext)
    preview_tabs(c,px+22,128,272,0)
    thumbnail(c,px+22,176,272,322,"coast",20)
    chip(c,px+22,518,82,"4032×3024",False,30,10)
    chip(c,px+112,518,68,"3.2 MB",False,30,10)
    chip(c,px+188,518,106,"10月1日 14:22",False,30,10)
    c.card(px+22,568,272,112,t.s1,r=18,stroke=False)
    c.text(px+38,594,"快速动作",size=12,weight=750)
    c.text(px+38,618,"属性、校验和与打开方式",size=11,fill=t.subtext)
    c.text(px+38,640,"已收进上方页签与尾部菜单",size=11,fill=t.subtext)
    icon(c,"chevR",px+270,622,18,t.subtext)
    button(c,px+22,812,128,46,"打开","filled","play",14)
    button(c,px+160,812,134,46,"共享","tonal",None,14)
    c.save(name)


def compact_browser(theme: Theme, name: str):
    c=Canvas(390,844,theme,f"Eidoria Compact 悬浮预览 · {'深色' if theme.dark else '浅色'}")
    t=theme
    c.rect(0,0,390,844,t.surface)
    c.text(18,25,"9:41",size=12,weight=700)
    c.text(372,25,"5G  84%",size=11,fill=t.subtext,anchor="end")
    icon(c,"list",28,58,21,t.text)
    c.text(54,64,"Pictures",size=21,weight=780)
    icon(c,"clock",326,58,20,t.subtext); icon(c,"settings",360,58,20,t.subtext)
    icon(c,"back",24,106,18,t.subtext); icon(c,"up",58,106,18,t.subtext)
    c.rect(84,86,190,40,t.s1,r=20,stroke=t.outline)
    icon(c,"folder",104,106,17,"#E9A93B"); c.text(120,111,"Pictures",size=12,weight=650)
    icon(c,"chevR",250,106,13,t.faint)
    search_island(c,286,86,"",42,40)
    icon(c,"dots",356,106,19,t.subtext)
    button(c,16,140,112,38,"新建文件夹","tonal","folder",11)
    button(c,136,140,88,38,"新文件","text","file",11)
    icon(c,"sliders",344,159,19,t.subtext); icon(c,"list",372,159,18,t.primary)
    yy=190
    for i in range(7):
        kind,nm,meta=FILES[i]
        file_row(c,10,yy,370,kind,nm,meta,selected=(i==1),h=60)
        yy+=64
    c.rect(0,74,390,676,"#000000",opacity=.18 if theme.dark else .10)
    fx,fy,fw,fh=14,150,362,560
    c.shadow(fx,fy,fw,fh,28,.22)
    c.card(fx,fy,fw,fh,t.s1,r=28,stroke=True)
    file_icon(c,"image",fx+16,fy+14,30)
    c.text(fx+54,fy+34,"IMG_20261001.jpg",size=13,weight=720)
    icon(c,"chevD",fx+304,fy+29,17,t.subtext); icon(c,"close",fx+336,fy+29,16,t.subtext)
    c.line(fx+16,fy+58,fx+fw-16,fy+58,t.outline,1)
    c.text(fx+22,fy+94,"IMG_20261001.jpg",size=18,weight=800)
    c.text(fx+22,fy+116,"JPG 文件 · 图像",size=11,fill=t.subtext)
    preview_tabs(c,fx+22,fy+130,fw-44,0)
    thumbnail(c,fx+22,fy+178,fw-44,248,"coast",20)
    chip(c,fx+22,fy+444,92,"4032×3024",False,30,10)
    chip(c,fx+122,fy+444,72,"3.2 MB",False,30,10)
    chip(c,fx+202,fy+444,118,"Camera 文件夹",False,30,10)
    button(c,fx+22,fy+490,150,46,"打开","filled","play",14)
    c.path(f"M {fx+fw-18} {fy+fh-6} L {fx+fw-6} {fy+fh-18} M {fx+fw-27} {fy+fh-6} L {fx+fw-6} {fy+fh-27}",stroke=t.faint,sw=2)
    c.text(18,742,"已选 1 项",size=11,fill=t.subtext)
    c.text(372,742,"排序：名称   任务",size=11,fill=t.subtext,anchor="end")
    c.rect(0,758,390,86,t.s1)
    nav=[("主页","home"),("收藏","star"),("回收站","trash"),("设置","settings")]
    for i,(label,ic) in enumerate(nav):
        cx=52+i*96
        if i==0: c.rect(cx-27,772,54,32,t.secondary_container,r=16)
        icon(c,ic,cx,788,20,t.on_secondary_container if i==0 else t.subtext)
        c.text(cx,826,label,size=11,anchor="middle",weight=650 if i==0 else 500)
    c.save(name)


def medium_floating():
    t=LIGHT; c=Canvas(768,1024,t,"Eidoria Medium 导轨与悬浮预览 · 浅色")
    c.rect(0,0,768,1024,t.bg)
    c.rect(0,0,88,1024,t.s1)
    app_icon(c,21,22,46)
    c.rect(16,88,56,56,t.primary_container,r=18); icon(c,"plus",44,116,26,t.on_primary_container,2.4)
    rail_items=[("主页","home"),("收藏","star"),("最近","clock"),("图片","image"),("视频","video"),("回收","trash"),("设置","settings")]
    yy=184
    for i,(label,ic) in enumerate(rail_items):
        if i==3: c.rect(16,yy-18,56,34,t.secondary_container,r=17)
        icon(c,ic,44,yy,21,t.on_secondary_container if i==3 else t.subtext)
        c.text(44,yy+31,label,size=11,anchor="middle",weight=650 if i==3 else 500)
        yy+=78
    c.card(108,20,640,984,t.surface,r=28,shadow=True)
    c.text(136,62,"Pictures",size=24,weight=820)
    c.text(136,86,"248 个项目 · 文件系统",size=12,fill=t.subtext)
    icon(c,"settings",714,56,21,t.subtext)
    icon(c,"back",148,126,18,t.subtext); icon(c,"forward",180,126,18,t.subtext); icon(c,"up",212,126,18,t.subtext)
    breadcrumb(c,240,105,330,["文件系统","Pictures","Camera"])
    search_island(c,584,106,"",42,40); icon(c,"dots",644,126,19,t.subtext)
    chip(c,136,166,74,"全部",True,34,12); chip(c,218,166,74,"图片",False,34,12); chip(c,300,166,74,"视频",False,34,12); chip(c,382,166,74,"文档",False,34,12)
    icon(c,"sliders",676,183,20,t.subtext); icon(c,"grid",710,183,20,t.primary)
    for i in range(9):
        kind,nm,meta=FILES[i]; r,col=divmod(i,3)
        file_card(c,136+col*146,222+r*176,132,160,kind,nm,meta.split(" · ")[0],selected=(i==1))
    fx,fy,fw,fh=382,238,336,620
    c.shadow(fx,fy,fw,fh,28,.18); c.card(fx,fy,fw,fh,t.s1,r=28)
    file_icon(c,"image",fx+16,fy+14,30); c.text(fx+54,fy+34,"IMG_20261001.jpg",size=13,weight=720)
    icon(c,"chevD",fx+278,fy+29,17,t.subtext); icon(c,"close",fx+310,fy+29,16,t.subtext)
    c.line(fx+16,fy+58,fx+fw-16,fy+58,t.outline,1)
    c.text(fx+22,fy+94,"IMG_20261001.jpg",size=18,weight=800)
    c.text(fx+22,fy+116,"JPG 文件 · 图像",size=11,fill=t.subtext)
    preview_tabs(c,fx+22,fy+130,fw-44,1)
    rows=[("类型","JPEG 图像"),("大小","3.2 MB"),("尺寸","4032 × 3024"),("修改时间","2026-10-01 14:22"),("位置","Pictures / Camera"),("路径","/home/user/Pictures/…")]
    yy=fy+196
    for k,v in rows:
        c.text(fx+22,yy,k,size=12,fill=t.subtext); c.text(fx+fw-22,yy,v,size=12,anchor="end",weight=580)
        yy+=34; c.line(fx+22,yy-18,fx+fw-22,yy-18,t.outline,1)
    c.card(fx+22,fy+420,fw-44,92,t.surface,r=18,stroke=False)
    icon(c,"shield",fx+48,fy+466,24,t.primary); c.text(fx+68,fy+458,"校验和",size=13,weight=720)
    c.text(fx+68,fy+480,"SHA-256 · 9F2C…A41E",size=11,fill=t.subtext,mono=True)
    button(c,fx+22,fy+542,146,46,"打开","filled","play",14)
    button(c,fx+178,fy+542,136,46,"复制路径","tonal",None,13)
    c.shadow(136,906,318,52,16,.16); c.rect(136,906,318,52,t.inverse,r=16)
    c.text(154,938,"已复制 3 个项目",size=12,fill=t.inverse_on)
    c.text(430,938,"撤销",size=12,fill=t.primary_container,anchor="end",weight=700)
    c.text(720,982,"已选 1 项 · D: 可用 210 GB",size=11,fill=t.subtext,anchor="end")
    c.save("05-medium-floating-preview-light")


def theme_board():
    c=Canvas(1440,900,LIGHT,"Eidoria 主题系统 · 深浅成对")
    c.rect(0,0,1440,900,"#CFE7DF")
    c.text(720,54,"Eidoria 主题系统",size=30,anchor="middle",weight=850,fill="#14231F")
    c.text(720,82,"同一布局、同一状态，只替换 Material 3 颜色角色",size=14,anchor="middle",fill="#52645E")
    panels=[(40,LIGHT,"浅色 · Light"),(730,DARK,"深色 · Dark")]
    for x,t,label in panels:
        c.t=t
        c.shadow(x,112,670,730,32,.16); c.card(x,112,670,730,t.surface,r=32,stroke=True)
        c.text(x+32,154,label,size=22,weight=820)
        c.text(x+638,154,"Seed #006A6A",size=12,fill=t.subtext,anchor="end",mono=True)
        c.card(x+32,176,606,212,t.s1,r=24,stroke=False)
        app_icon(c,x+52,196,38); c.text(x+100,212,"Pictures",size=17,weight=780); c.text(x+100,232,"248 个项目",size=11,fill=t.subtext)
        breadcrumb(c,x+250,196,250,["文件系统","Pictures"],38); search_island(c,x+514,197,"",42,38)
        file_row(c,x+52,252,270,"image","IMG_20261001.jpg","3.2 MB · 10月1日",True,56,False)
        file_row(c,x+52,316,270,"pdf","设计规范 v3.pdf","8.7 MB · 9月28日",False,56,False)
        thumbnail(c,x+344,252,262,112,"coast",16)
        button(c,x+32,420,126,44,"主要动作","filled","check",13)
        button(c,x+170,420,126,44,"色调按钮","tonal",None,13)
        button(c,x+308,420,126,44,"描边按钮","outline",None,13)
        button(c,x+446,420,100,44,"文字","text",None,13)
        toggle(c,x+574,430,True)
        chip(c,x+32,486,92,"全部",True,36,12); chip(c,x+134,486,92,"图片",False,36,12); chip(c,x+236,486,92,"文档",False,36,12)
        c.rect(x+346,486,292,36,t.s2,r=18)
        c.rect(x+350,490,142,28,t.secondary_container,r=14)
        c.text(x+421,509,"文件名",size=12,anchor="middle",weight=700); c.text(x+565,509,"内容",size=12,anchor="middle",fill=t.subtext)
        tokens=[("surface",t.surface),("surfaceContainer",t.s2),("primary",t.primary),("primaryContainer",t.primary_container),("secondaryContainer",t.secondary_container),("error",t.error)]
        yy=552
        for label2,col in tokens:
            c.rect(x+32,yy,42,28,col,r=8,stroke=t.outline)
            c.text(x+86,yy+19,label2,size=12,weight=600)
            c.text(x+638,yy+19,col,size=11,fill=t.subtext,anchor="end",mono=True)
            yy+=40
        c.card(x+32,796,606,28,t.secondary_container,r=14,stroke=False)
        c.text(x+335,815,"选中态使用容器色，不靠高饱和描边",size=11,anchor="middle",fill=t.on_secondary_container,weight=650)
    c.t=LIGHT
    c.save("00-theme-system-light-dark")


def check_row(c: Canvas, x, y, label, checked=True, sub=None):
    t=c.t
    c.rect(x,y-13,20,20,t.primary if checked else "none",r=6,stroke=t.primary if checked else t.outline)
    if checked: icon(c,"check",x+10,y-3,12,t.on_primary,2)
    c.text(x+30,y+2,label,size=12,weight=600 if checked else 500)
    if sub: c.text(x+30,y+19,sub,size=10,fill=t.subtext)


def search_center():
    t=DARK; c=Canvas(1440,900,t,"规划概念 · 搜索与索引中心 · 深色")
    desktop_window(c,"搜索中心",[("搜索中心","search")])
    c.card(306,18,1116,864,t.surface,r=26)
    section_title(c,336,66,"搜索中心","文件名与正文索引都在本机维护 · 不上传、不联网")
    c.rect(1260,38,132,34,t.secondary_container,r=17)
    icon(c,"shield",1280,55,17,t.on_secondary_container); c.text(1294,60,"仅本地",size=12,fill=t.on_secondary_container,weight=700)
    # Search field + modes
    c.shadow(336,104,760,60,30,.18); c.rect(336,104,760,60,t.s2,r=30,stroke=t.primary,sw=1.4)
    icon(c,"search",366,134,22,t.primary); c.text(392,141,"设计规范 2026",size=17,weight=650)
    c.text(1018,141,"Ctrl K",size=11,fill=t.subtext,anchor="end",mono=True); icon(c,"close",1068,134,16,t.subtext)
    c.rect(1120,104,272,60,t.s2,r=30)
    modes=[("文件名",True),("正文",False),("语义 · 后续",False)]
    xx=1128
    for label,sel in modes:
        w=78 if len(label)<5 else 104
        if sel: c.rect(xx,112,w,44,t.secondary_container,r=22)
        c.text(xx+w/2,140,label,size=11,anchor="middle",weight=700 if sel else 500,fill=t.on_secondary_container if sel else t.subtext)
        xx+=w+4
    # Filters
    c.card(336,190,228,622,t.s1,r=24,stroke=False)
    c.text(360,226,"筛选范围",size=15,weight=780)
    yy=260
    for label,sub,on in [("Pictures","248 个文件",True),("Documents","1,204 个文件",True),("主目录","仅文件名",False),("下载","未加入正文索引",False)]:
        check_row(c,360,yy,label,on,sub); yy+=58
    c.line(360,yy-8,540,yy-8,t.outline,1); yy+=24
    c.text(360,yy,"文件类型",size=13,weight=720); yy+=18
    chip(c,360,yy,68,"PDF",True,32,11); chip(c,436,yy,68,"文档",True,32,11); yy+=42
    chip(c,360,yy,68,"图片",False,32,11); chip(c,436,yy,68,"代码",False,32,11); yy+=64
    c.text(360,yy,"修改时间",size=13,weight=720); yy+=26
    for label in ("任意时间","本月","今年"):
        c.circle(370,yy-4,8,"none",t.primary if label=="本月" else t.outline,2)
        if label=="本月": c.circle(370,yy-4,3.5,t.primary)
        c.text(388,yy,label,size=12); yy+=34
    # Results
    x=588; c.text(x,222,"36 个结果",size=17,weight=800); c.text(1084,222,"12 ms · 索引命中",size=11,fill=t.subtext,anchor="end")
    results=[
        ("pdf","设计规范 v3.pdf","Documents / Specs","…采用响应式断点与悬浮搜索面板，确保 2026 版…",True),
        ("md","2026 规划.md","Documents / Notes","…搜索索引先行，双窗格进入 Phase 2 候选…",False),
        ("pdf","设计评审纪要.pdf","Downloads","…深色主题需要单独检查缩略图描边与反色面…",False),
        ("text","会议纪要.txt","Documents / Work","…索引根只由用户显式添加，默认不扫描全盘…",False),
        ("code","SearchIndex.cs","Projects / Eidoria","record IndexRoot(string Path, bool Content)",False),
    ]
    yy=242
    for kind,nm,path,snip,sel in results:
        c.card(x,yy,496,102,t.secondary_container if sel else t.surface,r=20,stroke=True)
        file_icon(c,kind,x+16,yy+16,44)
        c.text(x+72,yy+30,nm,size=14,weight=720,fill=t.on_secondary_container if sel else t.text)
        c.text(x+72,yy+51,path,size=11,fill=t.subtext)
        c.text(x+72,yy+78,snip,size=11,fill=t.on_secondary_container if sel else t.subtext)
        icon(c,"chevR",x+468,yy+51,17,t.subtext)
        yy+=114
    # Index health
    ix=1112
    c.card(ix,190,280,382,t.s1,r=24,stroke=False)
    icon(c,"database",ix+28,224,22,t.primary); c.text(ix+48,230,"索引健康",size=15,weight=780)
    c.text(ix+24,272,"128,430",size=28,weight=850); c.text(ix+24,294,"已索引文件",size=11,fill=t.subtext)
    progress(c,ix+24,318,232,.82,8); c.text(ix+24,342,"Documents 正在增量更新 · 82%",size=11,fill=t.subtext)
    rows=[("索引体积","186 MB"),("上次完整校准","今天 09:12"),("失效条目","12 · 可清理"),("正文索引","默认关闭")]
    yy=382
    for k,v in rows:
        c.text(ix+24,yy,k,size=11,fill=t.subtext); c.text(ix+256,yy,v,size=11,anchor="end",weight=620); yy+=34
    button(c,ix+24,514,108,40,"暂停","outline",None,12); button(c,ix+142,514,114,40,"管理根目录","tonal",None,12)
    c.card(ix,592,280,220,t.primary_container,r=24,stroke=False)
    icon(c,"lock",ix+28,626,21,t.on_primary_container); c.text(ix+48,632,"隐私边界",size=15,fill=t.on_primary_container,weight=780)
    c.text(ix+24,664,"正文只抽取白名单类型；",size=12,fill=t.on_primary_container)
    c.text(ix+24,686,"可随时暂停、清空或移除根目录。",size=12,fill=t.on_primary_container)
    c.text(ix+24,716,"索引不是备份，也不会改动原文件。",size=11,fill=t.on_primary_container,opacity=.82)
    button(c,ix+24,748,132,40,"清空索引","tonal",None,12)
    c.text(336,858,"结果来自本地索引 · 更新于 2 分钟前",size=11,fill=t.subtext)
    c.save("06-search-index-center-dark")


def details_table(c: Canvas, x, y, w, rows=7, active=True):
    t=c.t
    c.rect(x,y,w,34,t.s1,r=10)
    cols=[("名称",x+16),("修改日期",x+w*.54),("类型",x+w*.73),("大小",x+w-16)]
    for label,cx in cols: c.text(cx,y+22,label,size=11,fill=t.subtext,anchor="end" if label=="大小" else "start",weight=650)
    yy=y+44
    for i in range(rows):
        kind,nm,meta=FILES[(i if active else i+3)%len(FILES)]
        if i==1 and active: c.rect(x,yy-6,w,48,t.secondary_container,r=12)
        file_icon(c,kind,x+10,yy,34)
        c.text(x+54,yy+15,nm,size=11,weight=620 if i==1 and active else 520)
        c.text(x+54,yy+31,meta.split(" · ")[-1],size=9,fill=t.subtext)
        c.text(x+w*.54,yy+21,"2026-10-01",size=10,fill=t.subtext)
        c.text(x+w*.73,yy+21,kind.upper(),size=10,fill=t.subtext)
        c.text(x+w-16,yy+21,meta.split(" · ")[0],size=10,anchor="end")
        yy+=52


def dual_pane():
    t=LIGHT; c=Canvas(1600,1000,t,"规划概念 · ExtraLarge 双窗格 · 浅色")
    desktop_window(c,"图片")
    # Global top area
    c.card(306,18,1276,964,t.surface,r=26)
    c.rect(330,32,146,42,t.s2,r=13); icon(c,"folder",350,53,17,"#E9A93B"); c.text(366,58,"Pictures",size=12,weight=700)
    c.rect(486,32,146,42,t.surface,r=13,stroke=t.outline); icon(c,"file",506,53,17,t.subtext); c.text(522,58,"Documents",size=12,fill=t.subtext)
    c.circle(654,53,18,t.s1); icon(c,"plus",654,53,16,t.subtext)
    search_island(c,1394,34,"搜索",106,40); icon(c,"dots",1528,54,19,t.subtext)
    c.text(330,108,"双窗格",size=22,weight=820); c.text(430,108,"F6 切换活动窗格",size=11,fill=t.subtext,mono=True)
    button(c,1248,82,128,40,"同步导航","outline",None,12); toggle(c,1386,90,False)
    button(c,1450,82,108,40,"关闭 B","text",None,12)
    panes=[(330,"A · Pictures",True,["Pictures","Camera"]),(858,"B · Documents",False,["Documents","Work"])]
    for x,title,active,parts in panes:
        c.card(x,132,500,724,t.surface if active else t.s1,r=24,stroke=True,shadow=False)
        if active: c.rect(x,132,500,724,"none",r=24,stroke=t.primary,sw=2)
        c.circle(x+28,164,12,t.primary if active else t.s4)
        c.text(x+28,169,"A" if active else "B",size=11,anchor="middle",fill=t.on_primary if active else t.subtext,weight=800)
        c.text(x+48,169,title,size=15,weight=780 if active else 650,fill=t.text if active else t.subtext)
        c.text(x+476,169,"活动" if active else "后台会话",size=11,anchor="end",fill=t.primary if active else t.faint,weight=700)
        breadcrumb(c,x+20,188,360,parts,38)
        icon(c,"sliders",x+414,207,18,t.subtext); icon(c,"list",x+448,207,18,t.primary if active else t.subtext); icon(c,"grid",x+478,207,18,t.subtext)
        details_table(c,x+16,244,468,8,active)
        c.text(x+20,828,"248 个项目 · 已选 1 项" if active else "1,204 个项目 · 无选择",size=11,fill=t.subtext)
    # Drag ghost between panes
    c.shadow(754,430,190,104,20,.18); c.card(754,430,190,104,t.surface,r=20,stroke=True)
    thumbnail(c,768,444,52,52,"coast",12)
    c.text(830,466,"IMG_…jpg",size=12,weight=720); c.text(830,486,"移动到 B",size=11,fill=t.primary,weight=700)
    c.rect(768,502,96,20,t.secondary_container,r=10); c.text(816,516,"Ctrl = 复制",size=9,anchor="middle",fill=t.on_secondary_container,weight=650)
    c.path("M 830 416 C 850 390 884 390 906 416",stroke=t.primary,sw=2,dash="5 5")
    icon(c,"chevR",910,416,16,t.primary)
    # Preview follows active pane
    px=1382; c.card(px,132,176,724,t.s1,r=24,stroke=False)
    c.text(px+16,164,"预览 · A",size=13,weight=760)
    thumbnail(c,px+16,184,144,190,"coast",16)
    c.text(px+16,402,"IMG_20261001.jpg",size=11,weight=700)
    c.text(px+16,424,"3.2 MB · 4032×3024",size=10,fill=t.subtext)
    button(c,px+16,452,144,40,"打开","filled",None,12)
    c.line(px+16,522,px+160,522,t.outline,1)
    c.text(px+16,552,"跨窗格动作",size=12,weight=720)
    for yy,label in [(584,"移动到 B"),(622,"复制到 B"),(660,"在 B 中定位")]:
        icon(c,"chevR",px+22,yy-4,14,t.primary); c.text(px+38,yy,label,size=11)
    # Bottom hint
    c.card(330,880,1228,76,t.secondary_container,r=20,stroke=False)
    icon(c,"shield",358,918,22,t.on_secondary_container)
    c.text(382,912,"宽度跌回 Large 时，B 自动收为后台标签",size=13,fill=t.on_secondary_container,weight=720)
    c.text(382,936,"不丢路径、选集与滚动位置；再次展开即可恢复双窗格。",size=11,fill=t.on_secondary_container,opacity=.85)
    c.save("07-dual-pane-xlarge-light")


def donut(c: Canvas, cx, cy, r, segments):
    circ = 2 * math.pi * r
    c.circle(cx, cy, r, "none", c.t.s3, sw=24)
    before = 0.0
    for frac, col in segments:
        dash = f"{circ * frac:.1f} {circ * (1 - frac):.1f}"
        rot = -90 + 360 * before
        c.raw(
            f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="{col}" '
            f'stroke-width="24" stroke-dasharray="{dash}" transform="rotate({rot} {cx} {cy})"/>'
        )
        before += frac


def storage_insights():
    t=DARK; c=Canvas(1440,900,t,"规划概念 · 存储洞察与重复文件 · 深色")
    desktop_window(c,"存储洞察",[("存储洞察","chart")])
    c.card(306,18,1116,864,t.surface,r=26)
    section_title(c,336,66,"存储洞察","只读分析 · 任何删除都必须逐组复核")
    button(c,1174,38,104,40,"重新扫描","outline","refresh",12)
    button(c,1288,38,104,40,"导出报告","tonal",None,12)
    # Summary
    c.card(336,104,330,226,t.s1,r=24,stroke=False)
    donut(c,420,210,62,[(.42,"#8ADBC9"),(.25,"#FFB86B"),(.18,"#7AA2FF"),(.15,"#C792EA")])
    c.text(420,205,"68%",size=25,anchor="middle",weight=850); c.text(420,228,"已使用",size=11,anchor="middle",fill=t.subtext)
    c.text(516,150,"内部存储",size=15,weight=780); c.text(516,174,"87 GB / 128 GB",size=12,fill=t.subtext)
    rows=[("视频","#8ADBC9","36 GB"),("图片","#FFB86B","21 GB"),("文档","#7AA2FF","15 GB"),("其他","#C792EA","15 GB")]
    yy=206
    for label,col,val in rows:
        c.circle(522,yy-4,5,col); c.text(536,yy,label,size=11); c.text(642,yy,val,size=11,anchor="end",fill=t.subtext); yy+=28
    # Type distribution bars
    c.card(686,104,350,226,t.s1,r=24,stroke=False)
    c.text(712,140,"按类型分布",size=15,weight=780)
    bars=[("视频",.84,"36 GB","#8ADBC9"),("图片",.49,"21 GB","#FFB86B"),("压缩包",.21,"9 GB","#C792EA"),("音频",.14,"6 GB","#7AA2FF")]
    yy=168
    for label,ratio,val,col in bars:
        c.text(712,yy,label,size=11); progress(c,776,yy-8,190,ratio,8,col); c.text(1012,yy,val,size=10,anchor="end",fill=t.subtext); yy+=38
    c.text(712,306,"统计在后台任务中完成，可取消",size=11,fill=t.subtext)
    # Recoverable estimate
    c.card(1056,104,336,226,t.primary_container,r=24,stroke=False)
    c.text(1082,140,"可复核空间",size=15,fill=t.on_primary_container,weight=780)
    c.text(1082,196,"18.4 GB",size=38,fill=t.on_primary_container,weight=880)
    c.text(1082,224,"重复文件 6.2 GB · 大文件候选 12.2 GB",size=11,fill=t.on_primary_container,opacity=.85)
    c.card(1082,254,284,48,t.surface,r=16,stroke=False)
    icon(c,"shield",1104,278,19,t.primary); c.text(1124,274,"不自动删除",size=12,weight=730); c.text(1124,292,"先移入回收站也可恢复",size=10,fill=t.subtext)
    # Treemap
    c.card(336,354,700,300,t.s1,r=24,stroke=False)
    c.text(362,388,"空间地图",size=15,weight=780); c.text(1012,388,"点击块定位文件夹",size=11,anchor="end",fill=t.subtext)
    blocks=[(362,408,300,150,"#175E55","Videos","36 GB"),(674,408,170,150,"#7A4A19","Pictures","21 GB"),(856,408,154,70,"#2D4B8F","Documents","15 GB"),(856,486,154,72,"#5E3A75","Archives","9 GB"),(362,568,210,60,"#345E4A","Music","6 GB"),(584,568,260,60,"#3C4C59","Downloads","8 GB"),(856,568,154,60,"#5B4632","Cache","4 GB")]
    for x,y,w,h,col,label,val in blocks:
        c.rect(x,y,w,h,col,r=14); c.text(x+16,y+28,label,size=13,fill="#FFFFFF",weight=720); c.text(x+16,y+50,val,size=11,fill="#FFFFFF",opacity=.78)
    # Duplicate group
    dx=1056; c.card(dx,354,336,300,t.s1,r=24,stroke=False)
    c.text(dx+26,388,"重复文件 · 第 1 组",size=15,weight=780); c.text(dx+310,388,"98% 相同",size=11,anchor="end",fill=t.primary,weight=720)
    for i in range(3): thumbnail(c,dx+26+i*96,410,84,84,"coast" if i!=1 else "sunset",14)
    names=["IMG_0101.jpg","IMG_0101 (1).jpg","海边.jpg"]
    yy=526
    for i,nm in enumerate(names):
        c.circle(dx+34,yy-4,8,"none",t.primary if i==0 else t.outline,2)
        if i==0: c.circle(dx+34,yy-4,3.5,t.primary)
        c.text(dx+50,yy,nm,size=11,weight=650 if i==0 else 500)
        c.text(dx+310,yy,"保留" if i==0 else "候选",size=10,anchor="end",fill=t.primary if i==0 else t.subtext)
        yy+=30
    button(c,dx+26,606,132,38,"查看差异","outline",None,12); button(c,dx+168,606,142,38,"加入复核","tonal",None,12)
    # Large files
    c.card(336,678,1056,178,t.s1,r=24,stroke=False)
    c.text(362,712,"大文件 Top",size=15,weight=780); c.text(1366,712,"按大小排序",size=11,anchor="end",fill=t.subtext)
    larges=[("video","演示视频.mp4","Videos / 2026","380 MB",.92),("archive","backup-2026.zip","Downloads","1.4 GB",1),("pdf","图库导出.pdf","Documents","240 MB",.62)]
    yy=728
    for kind,nm,path,size,ratio in larges:
        file_icon(c,kind,362,yy,34); c.text(408,yy+16,nm,size=12,weight=650); c.text(408,yy+32,path,size=10,fill=t.subtext)
        progress(c,760,yy+14,340,ratio,7); c.text(1160,yy+21,size,size=11,anchor="end",weight=650)
        button(c,1192,yy-2,78,34,"定位","text",None,11); button(c,1278,yy-2,88,34,"预览","text",None,11)
        yy+=44
    c.save("08-storage-insights-dark")


def archive_workbench():
    t=LIGHT; c=Canvas(1440,900,t,"规划概念 · 压缩包工作台 · 浅色")
    desktop_window(c,"主页")
    c.card(306,18,1116,864,t.surface,r=26)
    file_icon(c,"archive",336,38,48)
    c.text(396,58,"backup-2026.zip",size=21,weight=820)
    c.text(396,80,"1.4 GB · 3,248 个条目 · ZIP",size=11,fill=t.subtext)
    c.rect(1120,40,118,34,t.secondary_container,r=17); icon(c,"shield",1140,57,16,t.on_secondary_container); c.text(1155,62,"只读浏览",size=11,fill=t.on_secondary_container,weight=700)
    button(c,1252,38,140,40,"解压全部","filled","archive",12)
    breadcrumb(c,336,102,520,["backup-2026.zip","photos","2026"],40)
    search_island(c,1186,102,"包内搜索",130,40); icon(c,"dots",1346,122,19,t.subtext)
    # Tree
    c.card(336,164,238,584,t.s1,r=24,stroke=False)
    c.text(360,198,"包内目录",size=14,weight=780)
    tree=[(0,"photos",False),(1,"2024",False),(1,"2025",False),(1,"2026",True),(0,"documents",False),(0,"projects",False),(1,"Eidoria",False),(0,"videos",False)]
    yy=232
    for level,label,sel in tree:
        x=360+level*22
        if sel: c.rect(352,yy-20,198,38,t.secondary_container,r=12)
        icon(c,"folder",x+12,yy,17,"#E9A93B"); c.text(x+26,yy+5,label,size=12,weight=650 if sel else 500)
        yy+=48
    c.line(360,610,550,610,t.outline,1)
    c.text(360,638,"压缩信息",size=12,weight=720)
    rows=[("原始大小","2.1 GB"),("压缩后","1.4 GB"),("压缩率","67%"),("加密","否")]
    yy=664
    for k,v in rows: c.text(360,yy,k,size=11,fill=t.subtext); c.text(550,yy,v,size=11,anchor="end",weight=620); yy+=24
    # Entries table
    x=598; c.card(x,164,492,584,t.surface,r=24,stroke=True)
    c.rect(x+16,184,460,36,t.s1,r=11)
    for label,cx,anch in [("名称",x+28,"start"),("原始",x+302,"end"),("压缩后",x+382,"end"),("日期",x+460,"end")]:
        c.text(cx,207,label,size=11,fill=t.subtext,anchor=anch,weight=650)
    entries=[("image","IMG_20261001.jpg","3.2 MB","3.1 MB","10-01",True),("image","海边落日.jpg","4.1 MB","4.0 MB","09-29",False),("video","旅行记录.mp4","380 MB","356 MB","09-15",False),("pdf","行程单.pdf","240 KB","198 KB","09-12",False),("text","说明.txt","18 KB","7 KB","09-10",False),("folder","RAW","—","—","09-08",False),("code","metadata.json","12 KB","4 KB","09-01",False),("audio","环境声.m4a","22 MB","21 MB","08-28",False)]
    yy=234
    for kind,nm,orig,comp,date,sel in entries:
        if sel: c.rect(x+12,yy-8,468,52,t.secondary_container,r=14)
        file_icon(c,kind,x+24,yy,36); c.text(x+70,yy+23,nm,size=12,weight=650 if sel else 540)
        c.text(x+302,yy+23,orig,size=11,anchor="end"); c.text(x+382,yy+23,comp,size=11,anchor="end",fill=t.subtext); c.text(x+460,yy+23,date,size=11,anchor="end",fill=t.subtext)
        yy+=60
    c.text(x+20,724,"已选 1 个条目 · 单文件可直接预览",size=11,fill=t.subtext)
    # Preview + extraction
    px=1114; c.card(px,164,278,584,t.s1,r=24,stroke=False)
    c.text(px+22,198,"条目预览",size=15,weight=780)
    thumbnail(c,px+22,218,234,190,"coast",18)
    c.text(px+22,436,"IMG_20261001.jpg",size=13,weight=720); c.text(px+22,458,"JPEG · 4032 × 3024",size=11,fill=t.subtext)
    c.line(px+22,484,px+256,484,t.outline,1)
    c.text(px+22,514,"解压到",size=12,weight=720)
    c.card(px+22,530,234,48,t.surface,r=14,stroke=False)
    icon(c,"folder",px+44,554,18,"#E9A93B"); c.text(px+60,559,"Pictures / backup-2026",size=11)
    c.text(px+22,606,"保留目录结构",size=12); toggle(c,px+204,594,True,.85)
    c.text(px+22,638,"覆盖同名文件前询问",size=12); toggle(c,px+204,626,True,.85)
    c.text(px+22,670,"解压后打开文件夹",size=12); toggle(c,px+204,658,False,.85)
    button(c,px+22,696,110,40,"解压所选","filled",None,12); button(c,px+142,696,114,40,"仅提取","outline",None,12)
    # Bottom task
    c.card(336,772,1056,70,t.secondary_container,r=20,stroke=False)
    icon(c,"archive",362,807,21,t.on_secondary_container)
    c.text(386,802,"正在解压 248 个文件",size=13,fill=t.on_secondary_container,weight=720)
    c.text(386,824,"Pictures / backup-2026 · 剩余约 42 秒",size=11,fill=t.on_secondary_container,opacity=.82)
    progress(c,760,802,430,.68,8,t.primary)
    c.text(1210,808,"68%",size=12,fill=t.on_secondary_container,weight=750)
    button(c,1280,788,88,38,"取消","text",None,12)
    c.save("09-archive-workbench-light")


def conflict_tasks():
    t=LIGHT; c=Canvas(1440,900,t,"规划概念 · 冲突解决与任务详情 · 浅色")
    desktop_window(c,"图片")
    c.card(306,18,1116,864,t.surface,r=26)
    # Simplified browser behind modal
    c.text(336,64,"Pictures",size=22,weight=820); breadcrumb(c,470,40,420,["Pictures","Camera"],40); search_island(c,1218,40,"搜索",106,40)
    yy=118
    for i in range(8):
        k,n,m=FILES[i]; file_row(c,336,yy,650,k,n,m,selected=(i==1),h=60); yy+=68
    # Task drawer visible at right behind modal edge
    tx=1040; c.card(tx,104,352,742,t.s1,r=26,stroke=True,shadow=True)
    c.text(tx+24,142,"任务中心",size=18,weight=820); c.text(tx+328,142,"3 个任务",size=11,anchor="end",fill=t.subtext)
    tasks=[("复制 248 个文件","等待确认 · 第 2/9 个冲突",.62,t.error),("解压 backup-2026.zip","排队中",0,t.subtext),("移动 12 张照片","已完成 · 2 分钟前",1,t.success),("计算校验和","失败 · 文件被占用",.34,t.error)]
    yy=168
    for title,sub,ratio,col in tasks:
        c.card(tx+18,yy,316,112,t.surface,r=20,stroke=False)
        c.text(tx+38,yy+30,title,size=13,weight=720); c.text(tx+38,yy+52,sub,size=11,fill=col if col==t.error else t.subtext)
        if ratio: progress(c,tx+38,yy+70,200,ratio,7,col if col!=t.subtext else t.primary)
        button(c,tx+250,yy+58,66,34,"重试" if "失败" in sub else "查看","text",None,11)
        yy+=126
    button(c,tx+24,788,142,40,"暂停全部","outline","pause",12)
    button(c,tx+176,788,142,40,"清除已完成","text",None,12)
    # Scrim and dialog
    c.rect(306,18,1116,864,"#0A1713",opacity=.28)
    dx,dy,dw,dh=350,142,660,616
    c.shadow(dx,dy,dw,dh,30,.24); c.card(dx,dy,dw,dh,t.surface,r=30,stroke=True)
    c.circle(dx+48,dy+48,24,t.error_container); icon(c,"shield",dx+48,dy+48,23,t.error)
    c.text(dx+84,dy+46,"目标已有同名文件",size=21,weight=830)
    c.text(dx+84,dy+70,"复制任务已暂停 · 第 2 / 9 个需要确认",size=12,fill=t.subtext)
    icon(c,"close",dx+dw-34,dy+42,17,t.subtext)
    # Compare cards
    cards=[("来源 · Camera","新文件",t.primary),("目标 · Pictures","已存在",t.warning)]
    for i,(label,badge,col) in enumerate(cards):
        x=dx+28+i*310
        c.card(x,dy+104,292,240,t.s1,r=22,stroke=False)
        c.text(x+18,dy+134,label,size=12,weight=740)
        c.rect(x+204,dy+116,70,26,t.secondary_container,r=13); c.text(x+239,dy+134,badge,size=10,anchor="middle",fill=t.on_secondary_container,weight=700)
        thumbnail(c,x+18,dy+150,256,104,"coast" if i==0 else "sunset",14)
        c.text(x+18,dy+280,"IMG_20261001.jpg",size=13,weight=720)
        c.text(x+18,dy+304,"3.2 MB · 2026-10-01 14:22" if i==0 else "2.8 MB · 2026-09-18 09:04",size=11,fill=t.subtext)
        c.text(x+18,dy+326,"SHA-256 9F2C…A41E" if i==0 else "SHA-256 71BD…03C8",size=10,fill=t.subtext,mono=True)
    # Choices
    choices=[("保留两者","新文件命名为 IMG_20261001 (2).jpg",True),("替换目标文件","旧文件先进入任务快照，可在任务详情中恢复",False),("跳过这个文件","继续处理其余 7 个冲突与后续队列",False)]
    yy=dy+372
    for title,sub,sel in choices:
        c.circle(dx+42,yy-4,10,"none",t.primary if sel else t.outline,2)
        if sel: c.circle(dx+42,yy-4,4.5,t.primary)
        c.text(dx+62,yy,title,size=13,weight=720 if sel else 600)
        c.text(dx+62,yy+20,sub,size=11,fill=t.subtext)
        yy+=48
    check_row(c,dx+32,yy+4,"对剩余全部同类冲突应用此选择",False)
    c.line(dx+28,dy+dh-76,dx+dw-28,dy+dh-76,t.outline,1)
    button(c,dx+28,dy+dh-58,112,42,"取消任务","danger",None,12)
    button(c,dx+dw-244,dy+dh-58,96,42,"稍后决定","text",None,12)
    button(c,dx+dw-138,dy+dh-58,110,42,"继续任务","filled",None,12)
    c.save("10-conflict-task-center-light")


def editor_pdf():
    t=DARK; c=Canvas(1440,900,t,"规划概念 · 文本轻编辑与 PDF 阅读工作台 · 深色")
    c.rect(0,0,1440,900,t.bg)
    c.shadow(18,18,1404,864,28,.3); c.card(18,18,1404,864,t.surface,r=28,stroke=True)
    # Workbench tabs
    app_icon(c,40,36,42); c.text(92,52,"预览工作台",size=18,weight=820); c.text(92,72,"只读默认 · 明确进入编辑后才可保存",size=11,fill=t.subtext)
    tabs=[("Program.cs",True,"code"),("设计规范 v3.pdf",False,"pdf"),("README.md",False,"md")]
    xx=390
    for label,sel,kind in tabs:
        w=150 if len(label)<12 else 190
        c.rect(xx,32,w,46,t.s2 if sel else t.surface,r=14,stroke=t.outline if not sel else None)
        file_icon(c,kind,xx+10,40,30); c.text(xx+48,60,label,size=12,weight=700 if sel else 540,fill=t.text if sel else t.subtext)
        if sel: c.line(xx+12,78,xx+w-12,78,t.primary,3)
        xx+=w+10
    icon(c,"dotsH",1362,55,20,t.subtext)
    # Editor
    ex=40; c.card(ex,102,650,742,t.s1,r=26,stroke=False)
    file_icon(c,"code",ex+20,122,38); c.text(ex+68,136,"Program.cs",size=15,weight=760)
    c.circle(ex+176,131,4,"#FFCF6B"); c.text(ex+188,136,"已修改",size=11,fill="#FFCF6B")
    button(c,ex+384,118,88,38,"朗读","outline","play",12)
    button(c,ex+482,118,72,38,"格式化","text",None,12)
    button(c,ex+562,118,68,38,"保存","filled",None,12)
    c.rect(ex+20,176,610,584,"#0B1512",r=18,stroke=t.outline)
    code=[
        ("1","using MdExplorer.UI;","#8ADBC9"),
        ("2","","#D6EDE7"),
        ("3","public async Task LoadAsync()","#D6EDE7"),
        ("4","{","#D6EDE7"),
        ("5","    await Browser.RefreshAsync();","#FFD166"),
        ("6","    Preview.Open(Browser.SelectedItem);","#D6EDE7"),
        ("7","    StatusText = \"已就绪\";","#A7F0DF"),
        ("8","}","#D6EDE7"),
        ("9","","#D6EDE7"),
        ("10","// 保存仍进入既有任务队列","#7E8F87"),
        ("11","await Jobs.EnqueueAsync(savePlan);","#D6EDE7"),
    ]
    yy=210
    for no,s,col in code:
        c.text(ex+48,yy,no,size=12,fill=t.faint,anchor="end",mono=True)
        c.text(ex+66,yy,s,size=13,fill=col,mono=True)
        yy+=35
    c.line(ex+66,yy+4,ex+66,yy+22,t.primary,2)
    c.text(ex+20,792,"UTF-8 · LF · 第 5 行，第 18 列",size=11,fill=t.subtext,mono=True)
    c.text(ex+630,792,"保存前显示差异",size=11,fill=t.primary,anchor="end",weight=650)
    c.text(ex+20,820,"外部修改会先提示冲突，不会静默覆盖。",size=11,fill=t.subtext)
    # PDF reader
    px=714; c.card(px,102,686,742,t.s1,r=26,stroke=False)
    file_icon(c,"pdf",px+20,122,38); c.text(px+68,136,"设计规范 v3.pdf",size=15,weight=760)
    c.text(px+68,154,"第 3 / 42 页 · 文字层可用",size=11,fill=t.subtext)
    button(c,px+466,118,86,38,"目录","outline","list",12); button(c,px+562,118,104,38,"用系统打开","tonal",None,12)
    # In-document search
    c.rect(px+20,176,520,44,t.s2,r=22,stroke=t.primary)
    icon(c,"search",px+46,198,19,t.primary); c.text(px+70,204,"响应式",size=13,weight=650)
    c.text(px+512,204,"2 / 18",size=11,fill=t.subtext,anchor="end",mono=True)
    icon(c,"close",px+574,198,17,t.subtext); icon(c,"chevD",px+610,198,17,t.subtext); icon(c,"chevR",px+644,198,17,t.subtext)
    # Outline
    c.card(px+20,240,150,520,t.surface,r=18,stroke=False)
    c.text(px+36,270,"目录",size=12,weight=720)
    outlines=[("1  目标",False),("2  断点",True),("3  导航",False),("4  预览",False),("5  动效",False),("6  无障碍",False)]
    yy=302
    for label,sel in outlines:
        if sel: c.rect(px+28,yy-19,126,32,t.secondary_container,r=10)
        c.text(px+38,yy,label,size=11,weight=650 if sel else 500,fill=t.on_secondary_container if sel else t.subtext); yy+=42
    # Pages (remain paper-light in dark theme)
    for i in range(2):
        x=px+190; y=240+i*268
        c.shadow(x,y,476,242,10,.2); c.rect(x,y,476,242,"#F7FAF8",r=10)
        c.text(x+28,y+36,"响应式布局必须先保证信息层级" if i==0 else "深色主题下仍保持纸张原色",size=17,fill="#17201D",weight=800)
        # Highlight search term inside a text-line composition
        c.rect(x+28,y+58,76,20,"#FFE58A",r=4); c.text(x+34,y+73,"响应式",size=12,fill="#17201D",weight=700)
        c.text(x+112,y+73,"断点决定导航形态，而不是简单缩放桌面界面。",size=12,fill="#3D4A45")
        for j in range(4): c.rect(x+28,y+96+j*22,420-(38 if j%2 else 0),7,"#CBD5D1",r=3)
        for j,bh in enumerate((42,68,52,82)): c.rect(x+34+j*42,y+214-bh,22,bh,"#2A9D8F",r=4)
        c.text(x+448,y+226,f"{3+i} / 42",size=10,fill="#61706A",anchor="end",mono=True)
    # Reader status
    c.card(px+190,782,476,42,t.inverse,r=21,stroke=False)
    c.text(px+218,808,"缩小",size=11,fill=t.inverse_on); c.text(px+428,808,"110%",size=12,fill=t.inverse_on,anchor="middle",weight=700); c.text(px+638,808,"放大",size=11,fill=t.inverse_on,anchor="end")
    c.save("11-editor-pdf-workbench-dark")


def media_player():
    t=DARK; c=Canvas(1440,900,t,"远期概念 · 内置媒体播放 · 深色")
    desktop_window(c,"视频")
    c.card(306,18,1116,864,"#090F0D",r=26,stroke=True)
    # Queue
    qx=330; c.card(qx,42,250,742,t.s1,r=24,stroke=False)
    c.text(qx+22,76,"播放队列",size=17,weight=800); c.text(qx+228,76,"12 项",size=11,anchor="end",fill=t.subtext)
    items=[("video","演示视频.mp4","12:48 · 380 MB",True),("video","海岸延时.mp4","03:26 · 96 MB",False),("audio","录音 001.m4a","08:12 · 22 MB",False),("audio","播客片段.mp3","24:05 · 45 MB",False),("video","产品发布.mp4","18:40 · 512 MB",False)]
    yy=98
    for kind,nm,meta,sel in items:
        if sel: c.rect(qx+12,yy,226,66,t.secondary_container,r=16)
        file_icon(c,kind,qx+24,yy+12,42); c.text(qx+76,yy+28,nm,size=11,weight=680 if sel else 540); c.text(qx+76,yy+47,meta,size=10,fill=t.subtext)
        yy+=78
    c.line(qx+22,510,qx+228,510,t.outline,1)
    c.text(qx+22,542,"播放设置",size=13,weight=720)
    for yy,label,on in [(576,"自动播放下一项",True),(612,"记住播放位置",True),(648,"后台继续音频",False)]:
        c.text(qx+22,yy,label,size=11); toggle(c,qx+188,yy-14,on,.8)
    # Stage
    sx=604; c.text(sx,66,"演示视频.mp4",size=20,weight=820); c.text(sx,88,"Videos / 2026 · 1920 × 1080 · H.264",size=11,fill=t.subtext)
    thumbnail(c,sx,112,790,444,"coast",24)
    c.circle(sx+395,334,48,"#07100E"); c.circle(sx+395,334,48,"none",t.primary_container,sw=2,opacity=.8); icon(c,"pause",sx+395,334,34,"#FFFFFF",3)
    c.rect(sx+176,474,438,36,"#07100E",r=10,opacity=.82)
    c.text(sx+395,498,"“我们先看三栏布局如何随窗口收放。”",size=13,fill="#FFFFFF",anchor="middle",weight=650)
    # Timeline chapters
    progress(c,sx,584,790,.38,8,t.primary)
    for ratio in (.16,.38,.63,.86): c.circle(sx+790*ratio,588,6,t.primary_container)
    c.text(sx,616,"04:52",size=11,fill=t.subtext,mono=True); c.text(sx+790,616,"12:48",size=11,fill=t.subtext,anchor="end",mono=True)
    chapters=[("开场",0),("响应式布局",.16),("搜索岛",.38),("任务中心",.63),("总结",.86)]
    for label,ratio in chapters: c.text(sx+790*ratio,640,label,size=10,anchor="middle",fill=t.primary if ratio==.38 else t.subtext,weight=650 if ratio==.38 else 500)
    # Controls
    controls=[("back",sx+280),("play",sx+395),("forward",sx+510)]
    for ic,cx in controls:
        big=ic=="play"; c.circle(cx,694,34 if big else 26,t.primary if big else t.s3); icon(c,"pause" if big else ic,cx,694,26 if big else 20,t.on_primary if big else t.text,2.4)
    icon(c,"sliders",sx+700,694,22,t.subtext); c.text(sx+790,700,"1.0×",size=12,anchor="end",weight=650)
    # Transcript / chapters card
    c.card(sx,742,790,104,t.s1,r=20,stroke=False)
    c.text(sx+22,772,"字幕",size=13,weight=740); c.rect(sx+72,758,86,26,t.secondary_container,r=13); c.text(sx+115,775,"中文 · 外挂 SRT",size=9,anchor="middle",fill=t.on_secondary_container,weight=650)
    c.text(sx+22,802,"04:52  我们先看三栏布局如何随窗口收放。",size=12,fill=t.primary)
    c.text(sx+22,826,"05:08  搜索入口收进迷你搜索岛，面板按需展开。",size=12,fill=t.subtext)
    button(c,sx+640,784,128,40,"导出字幕","outline",None,12)
    c.save("12-media-player-dark")


def extensions():
    t=DARK; c=Canvas(1440,900,t,"远期概念 · 插件中心与 Lua 脚本 · 深色")
    desktop_window(c,"扩展中心",[("扩展中心","puzzle")])
    c.card(306,18,1116,864,t.surface,r=26)
    section_title(c,336,66,"扩展中心","声明式插件 → 进程外工作者 → Lua 脚本（核心完善后）")
    c.rect(1212,38,180,36,t.secondary_container,r=18); icon(c,"shield",1232,56,17,t.on_secondary_container); c.text(1248,61,"宿主执行计划",size=11,fill=t.on_secondary_container,weight=700)
    # Installed extensions
    ex=336; c.card(ex,104,300,690,t.s1,r=24,stroke=False)
    c.text(ex+24,140,"已安装",size=16,weight=800); button(c,ex+190,116,86,36,"导入","outline","plus",12)
    exts=[("照片日期重命名","Lua 脚本 · v0.1.0","selection.read · plan.rename",True),("HEIC 预览工作者","进程外 · v1.2.0","preview.image",True),("深海主题包","声明式 · v2.0.1","theme.tokens",False),("压缩包校验","外部命令 · 桌面","command.run",False)]
    yy=162
    for name,sub,perm,on in exts:
        c.card(ex+16,yy,268,112,t.surface,r=18,stroke=False)
        icon(c,"puzzle",ex+42,yy+30,22,t.primary); c.text(ex+62,yy+28,name,size=13,weight=720); c.text(ex+62,yy+48,sub,size=10,fill=t.subtext)
        c.rect(ex+28,yy+68,196,24,t.s2,r=12); c.text(ex+38,yy+84,perm,size=9,fill=t.subtext,mono=True)
        toggle(c,ex+228,yy+66,on,.75); yy+=126
    c.card(ex+16,680,268,88,t.error_container,r=18,stroke=False)
    icon(c,"lock",ex+42,724,21,t.error); c.text(ex+62,716,"Android 商店版限制",size=12,fill=t.error,weight=740)
    c.text(ex+62,738,"不下载可执行代码",size=10,fill=t.error,opacity=.85)
    # Lua editor
    mx=660; c.card(mx,104,392,690,"#0B1512",r=24,stroke=True)
    c.text(mx+24,140,"main.lua",size=15,weight=780,mono=True); c.text(mx+368,140,"eidoria.script/1",size=10,anchor="end",fill=t.subtext,mono=True)
    code=[
        "local items = eidoria.selection()",
        "local plan = eidoria.plan.new()",
        "",
        "for _, item in ipairs(items) do",
        "  local date = item.taken_at or item.modified",
        "  local name = date .. '_' .. item.name",
        "  plan:rename(item.path, name)",
        "end",
        "",
        "return plan",
    ]
    yy=180
    for i,s in enumerate(code,1):
        c.text(mx+42,yy,str(i),size=11,fill=t.faint,anchor="end",mono=True)
        col="#8ADBC9" if s.startswith("local") or s.startswith("return") else "#D6EDE7"
        if "eidoria" in s: col="#FFD166"
        c.text(mx+56,yy,s,size=12,fill=col,mono=True); yy+=31
    c.line(mx+24,512,mx+368,512,t.outline,1)
    c.text(mx+24,546,"运行输出",size=13,weight=720)
    logs=["✓ 已读取 36 个选中文件","✓ 路径范围校验通过","✓ 生成 36 项重命名计划","等待用户确认，尚未写入磁盘"]
    yy=576
    for s in logs: c.text(mx+24,yy,s,size=11,fill=t.success if s.startswith("✓") else t.warning); yy+=28
    button(c,mx+24,712,116,42,"再次运行","outline","refresh",12)
    button(c,mx+150,712,116,42,"停止","danger",None,12)
    c.text(mx+368,738,"上限 2 秒 · 16 MB",size=10,anchor="end",fill=t.subtext,mono=True)
    # Plan review
    px=1076; c.card(px,104,316,690,t.s1,r=24,stroke=False)
    c.text(px+24,140,"操作计划确认",size=16,weight=800)
    c.rect(px+190,116,102,32,t.secondary_container,r=16); c.text(px+241,137,"未执行",size=11,anchor="middle",fill=t.on_secondary_container,weight=720)
    plans=[("IMG_0101.jpg","20261001_IMG_0101.jpg"),("海边落日.jpg","20260929_海边落日.jpg"),("IMG_0103.jpg","20261001_IMG_0103.jpg"),("旅行.mp4","跳过 · 非图片")]
    yy=168
    for old,new in plans:
        c.card(px+16,yy,284,86,t.surface,r=16,stroke=False)
        c.text(px+32,yy+26,old,size=11,fill=t.subtext)
        icon(c,"chevD",px+38,yy+48,13,t.primary); c.text(px+54,yy+66,new,size=11,weight=650,fill=t.primary if "跳过" not in new else t.subtext)
        yy+=98
    c.card(px+16,572,284,96,t.primary_container,r=18,stroke=False)
    icon(c,"shield",px+42,620,22,t.on_primary_container)
    c.text(px+62,604,"脚本只能提出计划",size=13,fill=t.on_primary_container,weight=750)
    c.text(px+62,628,"确认后才进入任务中心落盘",size=11,fill=t.on_primary_container,opacity=.85)
    button(c,px+24,692,122,44,"取消","text",None,12)
    button(c,px+154,692,138,44,"确认并执行","filled",None,12)
    c.text(336,838,"脚本异常只终止本次运行；越界路径会被宿主拒绝。",size=11,fill=t.subtext)
    c.save("13-extensions-lua-dark")


def local_ai():
    t=LIGHT; c=Canvas(1440,900,t,"远期概念 · 本地 AI 语义搜索 · 浅色")
    desktop_window(c,"本地 AI",[("本地 AI","spark")])
    c.card(306,18,1116,864,t.surface,r=26)
    section_title(c,336,66,"语义搜索","只搜索你明确加入索引的照片 · 模型与向量均在本机")
    c.rect(1206,38,186,36,t.secondary_container,r=18); icon(c,"shield",1226,56,17,t.on_secondary_container); c.text(1242,61,"离线可用 · 无遥测",size=11,fill=t.on_secondary_container,weight=700)
    # Query
    c.shadow(336,104,770,60,30,.12); c.rect(336,104,770,60,t.s1,r=30,stroke=t.primary,sw=1.4)
    icon(c,"spark",366,134,21,t.primary); c.text(392,141,"海边落日和帆船",size=17,weight=680)
    c.text(1078,141,"自然语言",size=11,anchor="end",fill=t.subtext)
    button(c,1130,104,118,60,"搜索图片","filled",None,13)
    button(c,1258,104,134,60,"模型与索引","outline","sliders",12)
    # Model/index sidebar
    mx=336; c.card(mx,190,248,622,t.s1,r=24,stroke=False)
    c.text(mx+24,226,"本地模型",size=15,weight=800)
    c.card(mx+16,246,216,126,t.surface,r=18,stroke=False)
    icon(c,"spark",mx+42,278,22,t.primary); c.text(mx+62,274,"SigLIP 2 Base",size=13,weight=730); c.text(mx+62,294,"int8 · 约 148 MB",size=10,fill=t.subtext)
    c.rect(mx+28,318,92,24,t.secondary_container,r=12); c.text(mx+74,334,"已下载",size=10,anchor="middle",fill=t.on_secondary_container,weight=700)
    toggle(c,mx+172,316,True,.8)
    c.text(mx+24,402,"照片索引",size=13,weight=740)
    c.text(mx+24,438,"24,810",size=27,weight=850); c.text(mx+24,460,"张图片已建立向量",size=11,fill=t.subtext)
    progress(c,mx+24,482,200,.91,7); c.text(mx+24,506,"Pictures · 更新于 12 分钟前",size=10,fill=t.subtext)
    c.line(mx+24,536,mx+224,536,t.outline,1)
    c.text(mx+24,568,"能力边界",size=13,weight=740)
    bullets=["标签只写入本地索引","不修改 EXIF 或原图","可删除模型与全部向量","Android 可限制仅充电时索引"]
    yy=598
    for s in bullets: icon(c,"check",mx+32,yy-4,13,t.primary); c.text(mx+48,yy,s,size=11); yy+=34
    button(c,mx+24,744,200,42,"管理索引根","outline",None,12)
    # Result grid
    gx=608; c.text(gx,222,"128 张相似照片",size=16,weight=800); c.text(1082,222,"按相关度排序",size=11,anchor="end",fill=t.subtext)
    scores=[96,94,91,89,87,84,82,79,76]
    kinds=["sunset","coast","mountain","sunset","coast","mountain","sunset","mountain","coast"]
    for i,(score,kind) in enumerate(zip(scores,kinds)):
        r,col=divmod(i,3); x=gx+col*162; y=242+r*188
        c.card(x,y,148,174,t.surface,r=18,stroke=True)
        thumbnail(c,x+8,y+8,132,104,kind,13)
        c.rect(x+92,y+16,48,24,t.inverse,r=12); c.text(x+116,y+32,f"{score}%",size=10,anchor="middle",fill=t.inverse_on,weight=750)
        c.text(x+12,y+134,f"海边_{i+1:03d}.jpg",size=11,weight=650)
        tags="落日 · 帆船" if i%3==0 else "海岸 · 天空"
        c.text(x+12,y+154,tags,size=10,fill=t.subtext)
        if i==0: c.rect(x,y,148,174,"none",r=18,stroke=t.primary,sw=2)
    # Selected AI card
    px=1110; c.card(px,190,282,622,t.s1,r=24,stroke=False)
    c.text(px+22,226,"识别结果",size=15,weight=800)
    thumbnail(c,px+22,246,238,190,"sunset",18)
    c.text(px+22,464,"海边_001.jpg",size=13,weight=720)
    c.text(px+22,486,"一艘帆船停在落日下的海面",size=11,fill=t.subtext)
    tags=[("落日",64),("帆船",64),("海面",64),("橙色天空",92)]
    xx=px+22; yy=510
    for label,w in tags:
        if xx+w>px+260: xx=px+22; yy+=40
        chip(c,xx,yy,w,label,False,32,11); xx+=w+8
    c.line(px+22,606,px+260,606,t.outline,1)
    c.text(px+22,638,"可继续问",size=12,weight=720)
    c.card(px+22,656,238,58,t.surface,r=16,stroke=False)
    c.text(px+38,682,"只看有帆船的照片",size=12); icon(c,"chevR",px+238,685,16,t.subtext)
    button(c,px+22,736,110,42,"找相似图","filled",None,12)
    button(c,px+142,736,118,42,"朗读说明","outline","play",12)
    c.save("14-local-ai-search-light")


def android_access_trash():
    t=DARK; c=Canvas(390,844,t,"规划概念 · Android 存储授权与应用内回收站 · 深色")
    c.rect(0,0,390,844,t.surface)
    c.text(18,25,"9:41",size=12,weight=700); c.text(372,25,"5G  84%",size=11,fill=t.subtext,anchor="end")
    icon(c,"back",28,60,20,t.text); c.text(54,66,"存储与回收站",size=20,weight=800); icon(c,"dots",366,60,19,t.subtext)
    # SAF authorization card
    c.card(16,88,358,286,t.s1,r=26,stroke=False)
    icon(c,"folder",42,120,22,"#E9A93B"); c.text(64,126,"已授权文件夹",size=16,weight=800)
    c.text(358,126,"SAF",size=10,anchor="end",fill=t.primary,weight=750,mono=True)
    grants=[("Download","读写 · 持久授权",t.success,"有效"),("Pictures","只读 · 持久授权",t.success,"有效"),("SD 卡 / Work","授权已失效",t.error,"重新授权")]
    yy=148
    for name,sub,col,action in grants:
        c.card(32,yy,326,62,t.surface,r=18,stroke=False)
        file_icon(c,"folder",44,yy+11,40); c.text(94,yy+25,name,size=13,weight=700); c.text(94,yy+45,sub,size=10,fill=col if col==t.error else t.subtext)
        c.text(342,yy+36,action,size=11,anchor="end",fill=col,weight=700)
        yy+=72
    c.text(32,368,"撤销后只移除访问权，不删除文件夹内容。",size=10,fill=t.subtext)
    # App trash
    c.card(16,392,358,306,t.s1,r=26,stroke=False)
    icon(c,"trash",42,424,21,t.primary); c.text(64,430,"应用内回收站",size=16,weight=800)
    c.rect(274,406,84,30,t.secondary_container,r=15); c.text(316,426,"规划中",size=11,anchor="middle",fill=t.on_secondary_container,weight=700)
    c.text(32,472,"1.8 GB",size=28,weight=860); c.text(150,472,"/ 5 GB 配额",size=12,fill=t.subtext)
    progress(c,32,488,326,.36,8)
    c.text(32,518,"30 天后自动清理",size=12); toggle(c,306,506,True,.85)
    trashed=[("image","IMG_0098.jpg","2 天前 · 3.1 MB"),("pdf","旧报价单.pdf","5 天前 · 840 KB"),("archive","backup-old.zip","12 天前 · 420 MB")]
    yy=540
    for kind,nm,meta in trashed:
        file_icon(c,kind,32,yy,38); c.text(80,yy+17,nm,size=12,weight=650); c.text(80,yy+34,meta,size=10,fill=t.subtext)
        c.text(306,yy+25,"还原",size=11,fill=t.primary,anchor="middle",weight=700); icon(c,"trash",346,yy+19,17,t.error)
        yy+=50
    # Actions
    button(c,16,718,164,46,"授权新文件夹","filled","folder",12)
    button(c,190,718,184,46,"清空回收站","danger","trash",12)
    c.rect(0,776,390,68,t.s1)
    nav=[("主页","home"),("收藏","star"),("回收站","trash"),("设置","settings")]
    for i,(label,ic) in enumerate(nav):
        cx=52+i*96; sel=i==3
        if sel: c.rect(cx-27,788,54,30,t.secondary_container,r=15)
        icon(c,ic,cx,803,19,t.on_secondary_container if sel else t.subtext)
        c.text(cx,832,label,size=10,anchor="middle",weight=650 if sel else 500)
    c.save("15-android-access-trash-dark")


def contact_sheet() -> None:
    """Compose a review contact sheet from rendered PNGs (optional Pillow)."""
    try:
        from PIL import Image, ImageDraw, ImageFont, ImageOps
    except ImportError:
        return
    items = [
        ("00-theme-system-light-dark", "主题系统", "设计基础"),
        ("01-large-browser-light", "Large · 浅色", "当前基线"),
        ("02-large-browser-dark", "Large · 深色", "当前基线"),
        ("03-compact-browser-light", "Compact · 浅色", "当前基线"),
        ("04-compact-browser-dark", "Compact · 深色", "当前基线"),
        ("05-medium-floating-preview-light", "Medium · 悬浮预览", "当前基线"),
        ("06-search-index-center-dark", "搜索与索引中心", "近期 M3"),
        ("07-dual-pane-xlarge-light", "ExtraLarge 双窗格", "Phase 2"),
        ("08-storage-insights-dark", "存储洞察", "近期 P2"),
        ("09-archive-workbench-light", "压缩包工作台", "近期增强"),
        ("10-conflict-task-center-light", "冲突与任务详情", "近期增强"),
        ("11-editor-pdf-workbench-dark", "编辑与 PDF 工作台", "Phase 2"),
        ("12-media-player-dark", "内置媒体播放", "远期"),
        ("13-extensions-lua-dark", "插件与 Lua", "最后"),
        ("14-local-ai-search-light", "本地 AI 语义搜索", "最后"),
        ("15-android-access-trash-dark", "Android 存储管理", "近期 P2"),
    ]
    cols, cell_w, cell_h, gap, header = 4, 620, 430, 24, 150
    rows = (len(items) + cols - 1) // cols
    width = gap + cols * (cell_w + gap)
    height = header + rows * (cell_h + gap) + gap
    sheet = Image.new("RGB", (width, height), "#DCEFEA")
    draw = ImageDraw.Draw(sheet, "RGBA")
    font_path = os.environ.get("EIDORIA_FONT_REGULAR") or "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    bold_path = os.environ.get("EIDORIA_FONT_BOLD") or "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
    try:
        title_font = ImageFont.truetype(bold_path, 42)
        sub_font = ImageFont.truetype(font_path, 22)
        label_font = ImageFont.truetype(bold_path, 23)
        tag_font = ImageFont.truetype(font_path, 18)
    except OSError:
        title_font = sub_font = label_font = tag_font = ImageFont.load_default()
    draw.text((gap, 34), "Eidoria 概念设计 v2", font=title_font, fill="#14231F")
    draw.text((gap, 92), "深浅主题成对 · 当前基线与下一阶段规划界面（概念图，不是应用实拍）", font=sub_font, fill="#52645E")
    for idx, (stem, label, tag) in enumerate(items):
        png = OUT / f"{stem}.png"
        if not png.exists():
            continue
        r, col = divmod(idx, cols)
        x = gap + col * (cell_w + gap)
        y = header + r * (cell_h + gap)
        draw.rounded_rectangle((x, y, x + cell_w, y + cell_h), radius=28, fill="#F8FAF8", outline="#BFD5CC", width=2)
        with Image.open(png) as im:
            thumb = ImageOps.contain(im.convert("RGB"), (cell_w - 28, cell_h - 82))
            tx = x + (cell_w - thumb.width) // 2
            ty = y + 14 + ((cell_h - 82) - thumb.height) // 2
            sheet.paste(thumb, (tx, ty))
        draw.text((x + 20, y + cell_h - 48), f"{idx:02d}  {label}", font=label_font, fill="#17201D")
        tw = draw.textlength(tag, font=tag_font) + 28
        tx = x + cell_w - tw - 18
        draw.rounded_rectangle((tx, y + cell_h - 52, tx + tw, y + cell_h - 18), radius=17, fill="#CDE8E1")
        draw.text((tx + 14, y + cell_h - 46), tag, font=tag_font, fill="#17423E")
    out = OUT / "contact-sheet.png"
    sheet.save(out, optimize=True)
    print(f"wrote {out} ({sheet.width}x{sheet.height})")


def update_canonical_mockups() -> None:
    """Keep the two historical root mockup paths pointed at the AI baselines.

    The vector set in OUT is only a structural reference.  When the directly
    generated AI mockups exist in the sibling ai-v3 directory, they win so a
    structure-draft rerun never replaces the public mockups with SVG exports.
    """
    import shutil

    root = OUT.parent
    ai_root = root / "ai-v3"
    large_ai = ai_root / "01-large-browser-light.png"
    large = large_ai if large_ai.exists() else OUT / "01-large-browser-light.png"
    if large.exists():
        shutil.copyfile(large, root / "mockup-large-desktop.png")
        print(f"updated {root / 'mockup-large-desktop.png'}")

    compact_ai = ai_root / "03-compact-browser-light.png"
    if compact_ai.exists():
        target = root / "mockup-compact-phone.png"
        shutil.copyfile(compact_ai, target)
        print(f"updated {target} (AI Compact)")
        return

    compact_svg = OUT / "03-compact-browser-light.svg"
    compact_png = OUT / "03-compact-browser-light.png"
    target = root / "mockup-compact-phone.png"
    try:
        import resvg_py  # type: ignore

        fonts = []
        for key in ("EIDORIA_FONT_REGULAR", "EIDORIA_FONT_BOLD"):
            v = os.environ.get(key)
            if v and Path(v).exists():
                fonts.append(v)
        kwargs = {"zoom": 2}
        if fonts:
            kwargs.update(
                font_files=fonts,
                font_family="Noto Sans CJK SC",
                sans_serif_family="Noto Sans CJK SC",
            )
        target.write_bytes(resvg_py.svg_to_bytes(svg_string=compact_svg.read_text(encoding="utf-8"), **kwargs))
    except Exception:
        if compact_png.exists():
            shutil.copyfile(compact_png, target)
        else:
            return
    print(f"updated {target} (2x Compact)")


def main():
    theme_board()
    desktop_browser(LIGHT, "01-large-browser-light")
    desktop_browser(DARK, "02-large-browser-dark")
    compact_browser(LIGHT, "03-compact-browser-light")
    compact_browser(DARK, "04-compact-browser-dark")
    medium_floating()
    search_center()
    dual_pane()
    storage_insights()
    archive_workbench()
    conflict_tasks()
    editor_pdf()
    media_player()
    extensions()
    local_ai()
    android_access_trash()
    contact_sheet()
    update_canonical_mockups()


if __name__ == "__main__":
    main()
