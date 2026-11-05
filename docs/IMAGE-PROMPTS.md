# MdExplorer 布局效果图 · GPT-image-2 提示词

> 用途：用 **GPT-image-2**（或同级文生图）生成各断点的高保真效果图作设计评审与宣发底稿。工程实现以 [响应式布局设计](RESPONSIVE-LAYOUT.md) 的 SVG 线框为准；本文件解决的是「图要好看且五张成套、控件与数据可逐一比对」的问题。

## 使用说明（先读）

1. **一图一喂**：每条提示词只生成一张单独的应用界面，不要要求在一张图里拼多机位/多尺寸对照（模型会把字打糊、比例失真）。
2. **先定锚图**：按 P-01（Compact）与 P-05（Large）各出到满意为止，保存为锚点图；其余提示词在原会话里以「保持与上一张完全相同的配色、图标风格与样例文件，仅按下列描述改布局」的方式接续生成，成套性显著强于全新会话。若平台支持参考图/图生图，直接附锚图并把改造话术写在提示词末尾的「接续说明」位。
3. **画布与参数**（经验值，平台名称不同按等效选）：质量最高档；Compact 竖版 `1024×1536` 或 `2:3`，Medium `3:4`，Expanded `3:2`，Large `16:10`，XLarge `21:9~16:9`；不需要透明底；一律「只画应用界面本体，不带设备边框/桌面背景/人手」。
4. **文字预期**：文生图的长英文/中文小字必有错字——提示词已尽量让标题短、字号大；验收只核对结构与关键标题，**交设计同事后以 SVG 线框为准重排文字**，不要因单个小字错误反复重抽烧额度。
5. **深色与多状态变体**：锚图定了浅色后，深色版用「同一界面切换为 Material 3 深色主题」的接续话术（见 P-09），勿重掷。

> **v3 覆盖说明（优先于下文旧提示词）：** 当前视觉基准以 [概念设计 v3](CONCEPT-DESIGN.md) 的 `assets/mockups/ai-v3/` 直接生成图为准，审计依据为 [hamen/material-3-skill](https://github.com/hamen/material-3-skill)。产品名为 **Eidoria（万象屿）**；文件夹页使用迷你搜索岛 + 悬浮搜索面板，预览使用 MD3 Secondary Tabs“预览 / 属性”，Compact/Medium 预览使用应用内悬浮窗。下文旧提示词只保留作历史探索；出现整行搜索框、账号头像、云盘、说明文字墙或内嵌胶囊文件页签时一律按 v3 纠正。

**v3 每条提示词必须追加的固定约束：**

```text
Apply a strict Material 3 audit: teal seed #006A6A, tonal surface containers instead of decorative shadows, secondaryContainer selection, outlineVariant dividers, 8dp spacing, 12dp cards, 28dp floating windows, 8dp chips, full-round buttons/search, and Title/Body/Label type hierarchy.

Desktop file tabs must match the original connected-tab concept: exactly one tab strip at the very top edge of the work surface; the active tab uses the same surface as the content and merges into it seamlessly; inactive tabs lie flat on a surfaceContainerLow shelf with a tiny icon, short title and close ×; a + follows. Never draw floating pill tabs, a segmented control, or a second tab strip inside a card/pane. Preview/Properties are MD3 secondary tabs with a slim indicator. On Compact, collapse file tabs into a stacked-tabs icon with a count badge.

Use an extreme text budget. Keep only the app name, short navigation labels, breadcrumb, filenames, search query, one numeric metadata value per file, and one short primary verb. Make toolbars, file types, storage categories, index state and permissions icon-only. Remove headings, feature descriptions, shortcut hints, status sentences, explanatory footers, type/date pairs and every word beside toolbar icons. Local files only; no cloud, account avatar, microphone, watermark or design annotations.
```

---

## 固定拼图段（每条提示词的前两段不要改，保证成套）

**① 全局风格段（英文版，模型对 UI 类英文提示服从度更高，是主力版本）：**

```text
High-fidelity UI design screenshot of a Material Design 3 (Material You) file manager app called MdExplorer, light theme, teal seed color #006A6A with mint tonal containers on a very light warm-white surface, large rounded corners (cards 16px, search bar fully pill-shaped, buttons pill-shaped), flat clean vector UI, soft subtle shadows, Material Symbols rounded icons, Roboto-like sans-serif typography, generous whitespace, professional product design, pixel-clean, no device frame, no desk background, app UI filling the whole canvas edge to edge.
```

**② 全局风格段（中文版，中文界面出图时用；界面文字即为简体中文）：**

```text
高保真 UI 设计稿：一款名为 MdExplorer 的 Material Design 3 风格文件管理器应用，浅色主题，青色种子色 #006A6A 生成的薄荷色调容器，近白色微暖背景；大圆角（卡片 16px、搜索栏与按钮为全胶囊形），扁平干净的矢量界面，柔和浅阴影，Material Symbols 圆角图标，无衬线中文排版，留白大方，专业产品设计感；只画应用界面本身铺满整个画面，不要设备边框、不要桌面背景、不要人手。
```

**③ 固定样例数据段（中英二选一随语言版本，五张图都用同一套文件名便于比对）：**

```text
Sample files (use exactly these names and values): folders "Documents, 12 items", "Pictures, 248 items"; files "IMG_20261001.jpg, 3.2 MB", "Design Spec v3.pdf, 8.7 MB", "Program.cs, 12 KB", "README.md, 4 KB", "backup-2026.zip, 1.4 GB", "Voice 001.m4a, 22 MB", "Demo video.mp4, 380 MB", "meeting-notes.txt, 18 KB". Storage card: "Internal storage, 87 GB of 128 GB used" with a teal progress bar. Current location breadcrumb: "Internal storage > Pictures".
```

```text
样例文件（请原样使用这些名称与数值）：文件夹「Documents，12 个项目」「Pictures，248 个项目」；文件「IMG_20261001.jpg，3.2 MB」「设计规范 v3.pdf，8.7 MB」「Program.cs，12 KB」「README.md，4 KB」「backup-2026.zip，1.4 GB」「录音 001.m4a，22 MB」「演示视频.mp4，380 MB」「会议纪要.txt，18 KB」。存储卡文案：「内部存储，已用 87 GB / 128 GB」，青色进度条。当前路径面包屑：「内部存储 > Pictures」。
```

**通用反向提示（平台有 Negative 栏则填，无则把首句追加在正向末尾）：**

```text
No phone frame, no laptop, no hands, no desk scene, no cloud drive, no Google Drive, no user avatar, no gradients blobs, no purple theme, no glassmorphism, no tiny unreadable micro text walls, no watermark.
```

---

## P-01 · Compact 手机竖屏（<600 dp，锚图①）

**拼装：全局风格段（英或中）+ 样例数据段 + 下列描述**

```text
Smartphone portrait layout, single column. Top: small app bar with a hamburger menu icon, title "Files", overflow dots icon on the right. Below: a full-width pill-shaped search bar with a magnifier icon and placeholder "Search files". Below: a rounded storage card showing internal storage usage with a teal progress bar. Below: a horizontally scrollable row of rounded category chips: Images, Videos, Audio, Documents, Downloads, Archives. A breadcrumb row "Internal storage > Pictures" with sort and grid-view icons at the right. Main area: a comfortable two-line file list, each row a leading icon or thumbnail (yellow folder icons, a photo thumbnail, red PDF badge, generic file icons), file name on the first line, "size · date" meta on the second line in gray, and a vertical three-dot menu at the row end. A rounded-square mint floating action button with a plus sign floats at the bottom right. Bottom: a Material 3 navigation bar with four items — Home (selected, mint pill highlight), Recent, Favorites, Settings.
```

中文描述版：

```text
手机竖屏单栏布局。顶部小应用栏：左汉堡菜单图标，标题「文件」，右竖三点图标。下方通栏胶囊搜索栏带放大镜图标与占位文字「搜索文件」。再下为圆角存储卡片（内部存储用量与青色进度条）。再下为一排可横向滚动的类别芯片：图片、视频、音频、文档、下载、压缩包。面包屑一行「内部存储 > Pictures」，右侧排序与网格视图小图标。主体为舒适的两行式文件列表：每行左侧图标或缩略图（黄色文件夹、照片缩略图、红色 PDF 徽标、通用文件图标），第一行文件名，第二行灰色「大小 · 日期」，行尾竖三点菜单。右下角悬浮方圆角薄荷色新建按钮（加号）。底部为 Material 3 导航栏四项：主页（选中带薄荷色胶囊高亮）、最近、收藏、设置。
```

## P-02 · Compact 选择模式（接续 P-01）

```text
Same screen and same file list as the reference image, now in multi-selection mode: two files are selected with teal checkmark avatars replacing their icons and lightly tinted row backgrounds; the top app bar is replaced by a contextual action bar on a mint container background reading "2 selected" with a close X on the left and share, delete and overflow icons on the right; the floating action button and bottom navigation bar are hidden. Everything else unchanged.
```

中文接续版：`与参考图完全同一界面同一文件列表，现为多选状态：两个文件行首图标变为青色勾选标记、行底淡色高亮；顶栏替换为薄荷色上下文操作栏（左叉号、「已选 2 项」、右侧共享删除更多图标）；悬浮新建按钮与底部导航隐藏；其余不变。`

## P-03 · Medium 平板竖屏（600–839 dp）

```text
Tablet portrait layout. A narrow 80dp navigation rail on the left edge on a very light mint-gray container: a rounded-square mint floating action button with a plus sign at the top of the rail, below it vertically stacked nav items with icon-over-label (Home selected with a mint pill behind the icon, Recent, Favorites, Gallery, Trash, Settings). Right of the rail: small app bar with title "Files", then a wide pill-shaped search bar, then a breadcrumb row "Internal storage > Pictures" and a row of filter chips (All, Images, Videos, Audio, Documents, Archives). Main area: a 4-column file grid; each cell has a large rounded thumbnail tile (folder, photos of a teal coastline, PDF and document icons) with the file name and size centered under it. A dark Material snackbar at the bottom left of the content area reads "3 items copied" with an "Undo" action.
```

中文描述版：

```text
平板竖屏布局。最左侧 80dp 宽的浅薄荷灰导轨：顶部是方圆角薄荷色新建按钮，下方图标在上文字在下的竖排导航项（主页选中带薄荷胶囊，其后最近、收藏、图库、回收站、设置）。导轨右侧：小应用栏标题「文件」；通宽胶囊搜索栏；面包屑一行「内部存储 > Pictures」与筛选芯片行（全部、图片、视频、音频、文档、压缩包）。主体为 4 列文件网格：每格上方大圆角缩略图块（文件夹、青色海岸照片、PDF 与文档图标），下方居中文件名与大小。内容区左下角有一条深色 Material 提示条：「已复制 3 个项目」带「撤销」动作。
```

## P-04 · Expanded 平板横屏/桌面小窗（840–1199 dp）

```text
Landscape large-tablet / small desktop window layout, two zones. Left: a permanent navigation drawer (about one quarter width) on a light mint-gray container, app name "MdExplorer" at top, a mint extended "New" button, nav list (Home selected with rounded pill highlight, Favorites, Recent, Images, Videos, Documents), a divider, a "Storage" group with two drives and small usage bars, then Trash and Settings pinned lower. Right main zone: top row with back/forward/up arrows, a pill search bar in the middle with placeholder "Search in Pictures", and view icons on the right; below it a clickable breadcrumb "Internal storage > Pictures > Camera" and an item count on the right; main area split into a two-line file list (same sample files) and a preview side pane on the right showing a large photo of a teal coastline, the file name "IMG_20261001.jpg", a metadata table (Type JPEG image, Size 3.2 MB, Dimensions 4032 × 3024, Modified date, Location) and three buttons "Open" (filled teal), "Share", "More". A slim status bar at the very bottom of the main zone reads "1 item selected" on the left and "Copying… 62%" on the right.
```

中文描述版：

```text
平板横屏或桌面小窗口布局，左右两区。左侧常驻导航抽屉（约四分之一宽、浅薄荷灰底）：顶部应用名「MdExplorer」、薄荷色「＋新建」大按钮、导航列表（主页选中带圆角胶囊高亮、收藏、最近使用、图片、视频、文档），分隔线下「存储设备」组（两个盘符带小用量条），下部回收站与设置。右侧主区：首行前进后退向上箭头、居中胶囊搜索栏（占位「在 Pictures 中搜索」）、右侧视图图标；下为可点面包屑「内部存储 > Pictures > Camera」与右侧计数；主体左右再分：左边两行式文件列表（同前述样例文件），右边预览侧栏——大幅青色海岸照片、文件名「IMG_20261001.jpg」、元数据表格（类型 JPEG 图像、大小 3.2 MB、尺寸 4032×3024、修改时间、位置）与三个按钮（青色实心「打开」与「共享」「更多」）。主区最底部细状态条：左「已选 1 项」，右「正在复制…62%」。
```

## P-05 · Large 桌面（1200–1599 dp，锚图②，重要性同 P-01）

```text
Desktop application window, 16:10. Left permanent navigation drawer in light mint-gray with app name "MdExplorer", a mint "New" button, nav items (Home selected, Favorites, Recent, Images, Videos, Documents, Trash), a "Drives" section listing only local drives with usage bars (no cloud drives), Settings at the bottom. Main zone top: a browser-like tab strip with two folder tabs "Pictures" (active) and "Documents" each with a small x, plus a + new tab button. Below: navigation row with back/forward/up/refresh arrows, a wide rounded breadcrumb pill "This PC > Data (D:) > Pictures > Camera", and a pill search field on the right with placeholder "Search in Camera (Ctrl+K)". Below: a slim command toolbar with icon+text actions: New, Cut, Copy, Paste, Rename, Delete, then Sort, View, More on the right. Main area: a 6-column grid of file cards with large thumbnails (two folders, coastline and sunset photos, PDF and Word-like document tiles), one card selected with a teal outline. Right: preview pane with the selected coastline photo large, file name, metadata rows, and filled "Open" plus tonal "Share" buttons. Bottom status bar: "248 items · 1 selected (3.2 MB)" left, "D: 210 GB free" right.
```

中文描述版：

```text
桌面应用窗口 16:10。左侧浅薄荷灰常驻导航抽屉：应用名「MdExplorer」、薄荷色「新建」按钮、导航项（主页选中、收藏、最近、图片、视频、文档、回收站）、「磁盘」组仅列本地盘与用量条（不要任何云盘）、底部设置。主区顶部：浏览器式标签行（活动标签「Pictures」、次标签「Documents」各带小叉、末尾加号新建标签）。下一行导航行：前进后退向上刷新箭头、居中宽胶囊面包屑「此电脑 > 数据盘 (D:) > Pictures > Camera」、右侧胶囊搜索框占位「在 Camera 中搜索 (Ctrl+K)」。再下一条细命令工具栏（图标加文字：新建、剪切、复制、粘贴、重命名、删除，右侧排序、视图、更多）。主体为 6 列文件卡片网格：两个文件夹、海岸与日落照片、PDF 与文档磁贴，其中一张选中带青色描边。右侧预览栏：选中海岸大图、文件名、元数据行、青色实心「打开」与浅色「共享」按钮。底部状态栏左「248 个项目 · 已选 1 项 (3.2 MB)」，右「D: 可用 210 GB」。
```

## P-06 · ExtraLarge 双窗格（≥1600 dp，接续 P-05）

```text
Same desktop app, same drawer, tabs, breadcrumb, search and toolbar as the reference image, but on an extra-wide 21:9 canvas and with dual-pane mode enabled: the main area now shows two detailed file-list panes side by side, left pane titled "Pane A · Pictures" with column headers Name / Date modified / Type / Size and dense two-line rows, right pane titled "Pane B · Documents" with the same columns and a slightly muted inactive look; the preview pane stays on the far right following the active left pane's selection; the status bar mentions both panes' item counts. Everything else identical in style and sample data.
```

中文接续版：`与参考图同一桌面应用（抽屉、标签、面包屑、搜索、工具栏不变），画布改为 21:9 超宽并开启双窗格：主区分左右两个详细信息列表窗格，左「窗格 A · Pictures」带列头（名称/修改日期/类型/大小）与紧凑两行条目，右「窗格 B · Documents」同列头且呈非激活的稍淡状态；最右仍为预览栏并跟随左侧活动窗格的选集；状态栏同时交代两窗格计数；其余风格与样例数据完全一致。`

## P-07 · 全屏搜索（Compact，接续 P-01）

```text
Same phone portrait app as the reference, now showing the full-screen search view: at top a search field with a back arrow and the typed query "report", below it two recent-search rows with clock icons ("report", "invoice"), then a row of filter chips (All, Documents, PDF, This month), then search results as two-line list rows where each result shows the file name with the matched part in teal bold and the parent path "Documents / Work" in small gray text under the name; keyboard is NOT shown. Same teal Material 3 style and sample data family.
```

中文接续版：`与参考图同机型的全屏搜索界面：顶部搜索框内有返回箭头且已输入「报告」，下方两行带时钟图标的最近搜索（报告、发票），一行筛选芯片（全部、文档、PDF、本月），结果为两行列表（文件名命中段青色加粗，名下灰色小字父路径如「Documents / Work」），不画键盘；保持同一青色 Material 3 风格与样例数据家族。`

## P-08 · PDF 预览（接续任一图，小白鼠页面共用）

```text
Same Material 3 file manager style, now a full-screen document preview page: top app bar with back arrow, title "Design Spec v3.pdf", page indicator "3 / 42" and overflow menu; the center shows one clean white PDF page with a bold heading, several gray paragraph lines and a simple teal bar chart graphic, subtle page shadow on the light background; bottom a floating pill toolbar with zoom out, zoom percentage "100%", zoom in, and a filled teal "Open" button at the right end.
```

中文版：

```text
同一 Material 3 文件管理风格，现为全屏文档预览页：顶栏返回箭头、标题「设计规范 v3.pdf」、页码指示「3 / 42」与溢出菜单；中央一页干净的白纸 PDF（粗体标题、数行灰色段落线、一个简易青色条形图），浅底上带纸张柔影；底部悬浮胶囊工具条（缩小、「100%」、放大）右端为青色实心「打开」按钮。
```

## P-09 · 深色变体（对任意锚图接续）

```text
Recreate the exact same screen, same layout, same sample files and text, but switch to the Material Design 3 dark theme derived from the same teal seed: dark charcoal-teal surfaces (#0E1514 family), slightly lighter surface containers, mint-teal primary accents and soft mint selected states, white primary text and gray-green secondary text, thumbnails unchanged, no layout changes at all.
```

中文接续版：`完全同一界面、布局、样例文件与文字，仅切换为由同一青色种子生成的 Material 3 深色主题：深炭青表面、稍提亮的容器层级、薄荷青强调与选中态、白色主文本与灰绿次级文本，缩略图不变，不动任何布局。`

## P-10 · 权限引导空态（Android 首启，接续 P-01）

```text
Same phone app style, showing a friendly first-run permission empty state instead of a file list: a large rounded illustration area with a simple teal folder-and-key line illustration, headline "Choose a folder to get started", two lines of small explanatory text about picking folders with the system picker and being able to revoke access anytime, a wide filled teal pill button "Grant folder access", and a text button below "Browse app folder instead". Bottom navigation bar remains.
```

中文版：

```text
同一手机应用风格，现显示友好的首启授权空态而非文件列表：大块圆角插图区为简易青色线条插画（文件夹加钥匙），大标题「选择一个文件夹开始」，两行小字说明将经系统选择器授权且可随时在系统里撤销，一个通栏青色实心胶囊按钮「授权文件夹访问」，下方文字按钮「先看应用文件夹」；底部导航栏保留。
```

## P-11–P-18 · v2 规划界面增补（均接续 v2 锚图）

统一前置：`保持 Eidoria（万象屿）同一 Material 3 青色种子、中文界面、样例文件与深浅主题角色；只画一张应用界面，不带设备框；文件夹页保留迷你搜索岛，预览保留“预览 / 属性”双页签；禁止云盘、账号头像、联网 AI 与紫色玻璃拟态。`

- **P-11 搜索与索引中心（深色）**：`桌面三列：左为索引根/类型/时间筛选，中为带父路径与正文片段的结果卡，右为索引健康与隐私边界；顶部大搜索框查询“设计规范 2026”，模式分段为文件名/正文/语义·后续。`
- **P-12 ExtraLarge 双窗格（浅色）**：`同一抽屉下并排 A/B 两个完整文件窗格，各有面包屑与详细信息表；A 为活动窗格主色描边，一张图片拖影从 A 指向 B 并写明 Ctrl=复制；最右窄预览跟随 A。`
- **P-13 存储洞察（深色）**：`总容量环、类型条、空间地图、重复文件组与大文件 Top 同屏；强调“不自动删除”，重复项只给保留/候选与加入复核，不出现一键清理。`
- **P-14 压缩包工作台（浅色）**：`把 ZIP 当作只读虚拟文件夹：左侧包内目录树，中间条目表含原始/压缩后大小，右侧条目图片预览与解压选项，底部是可取消的解压进度条。`
- **P-15 冲突与任务详情（浅色）**：`浏览器背景上居中冲突对话框，来源与目标缩略图/大小/时间/校验和并排比对，单选保留两者/替换/跳过；右侧任务中心同时显示等待确认、排队、完成与失败重试。`
- **P-16 编辑与 PDF 工作台（深色）**：`左侧代码轻编辑带已修改标记、行号与保存前差异提示；右侧 PDF 连续两页、目录与文档内搜索高亮，深色应用中 PDF 纸张保持白色。`
- **P-17 插件/Lua 与本地 AI（最后轨道，二选一出图）**：`插件版：已安装扩展与权限、Lua 代码、运行日志、旧名到新名的操作计划确认四区同屏。AI 版：自然语言搜图、相关度网格、本地模型与索引状态、识别标签，所有状态写明离线且可删除模型。`
- **P-18 Android 存储管理（Compact 深色）**：`已授权文件夹逐项显示读写/只读/失效状态，下方应用内回收站显示配额、30 天自动清理和待还原文件；授权新文件夹为唯一实心主按钮。`

---

## 出图验收单（逐张过，错一处在接续话术里定点修）

- [ ] 只有本地存储（无云盘/网盘字样与图标）
- [ ] 断点形态对（导航形态、列数与 [布局文](RESPONSIVE-LAYOUT.md) 对应图一致）
- [ ] 样例文件名/数值大体正确（小字错字可忍受，标题错不行）
- [ ] 青色系无跑偏（忌蓝紫玻璃拟态），选中态为薄荷容器色而非描粗边夸张投影
- [ ] 关键入口齐全：搜索恒近顶部、主动作（FAB/新建）单手或工具栏可达、预览图元信息动作俱全
- [ ] 五张成套：同会话接续、无一图换字体/换图标语言

> **备注**：若手头没有 GPT-image-2 额度，仓库已置完整的 v2 矢量概念套图（`docs/assets/mockups/v2/`，16 张 + 总览）和五张精确线框（`docs/assets/layouts/`），评审与开发自定向不缺图；只有在需要换一种高保真探索时才按本提示词补图。

