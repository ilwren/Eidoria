# Eidoria（万象屿）品牌与图标规范

## 1. 名称

产品正式显示名为 **Eidoria（万象屿）**，读作 *eye-DOR-ee-a*。`MdExplorer` 继续保留为内部工程代号。

- **Eidoria**：由希腊语 *eidos*（形相、形象、观念）与带有“境域 / 收藏之地”意味的 *-oria* 拼合而成，意为“万象栖居之地”。它不直译“文件管理器”，而是把本地文件看作照片、文档、代码、声音与记忆的万千形相。
- **万象屿**：中文名取“万象”与“屿”。文件系统像一座由路径相连的岛屿，文件夹是地层，文件树向上生长，预览则让内容从岛上浮现。
- 品牌语气：安静、策展式、带有自然史标本馆与小型美术馆的气质；避免企业软件式的功能直译命名。

本轮曾淘汰第一版显示名 Layerfold：语义准确但过于字面，缺少独立世界观。其他候选如 Folio、Materio、ChromaFold、Papyra、Treefold、Sylvaris、Velluna、Scrinium、Herbarium、Foliora 与 Vellichor 均已有软件或相近产品使用。`Eidoria` 在本轮公开网页浅层排查中未检到同名软件产品；正式发布前仍应做商标、应用商店与域名清查。

## 2. 图标构造

主稿：`docs/assets/brand/eidoria-icon.svg`（1024×1024）。

新版回到早期概念稿左上角的折页小图标，并把它矢量化、放大为独立应用图标：

- **蓝色竖页**：主体是一张竖向圆角页片，先读作“文件”，但不用直白的文件夹轮廓；大面积蓝色保证 16px 下仍有稳定剪影。
- **左上翻角**：左上角沿一条弧线翻起，动作来自“预览正在掀开内容”；折线带轻微弧度，不用生硬的直角狗耳。
- **绿色背页**：被掀开的位置露出绿色背页，形成绿 → 橙 → 蓝的单向色彩转折，也是小尺寸下最先被认出的记号。
- **橙黄折面**：翻起的纸角以金黄到橙色的单向渐变收成一个宽折面，只保留一条高光和一层很轻的落影，不做拟物纸纹。
- **错落的后页**：主页面右下方露出半张薄荷色页片，暗示文件层叠、标签页与目录层级，但不把主体画成文件夹。
- **页脚地层**：蓝色页脚压两道低对比曲线，保留“万象屿”的岛屿地层感；它们只作气氛，不承担识别。
- 外轮廓为约 22.5% 半径的 squircle，底色由近白薄荷过渡到浅青；桌面主稿图形收在画布约 14%–87% 区间，Android adaptive 前景另按 108dp 画布收进约 19%–82% 区间。
- 不使用字母、产品文字、直白文件夹、文件树节点或星光贴纸；品牌识别来自“蓝页 + 绿背 + 橙折角”的三角组合。

## 3. 核心色

| 角色 | Hex | 说明 |
|---|---:|---|
| Pale field | `#D8F1EC` | 图标浅色场中点（主稿由 `#EDFBF4` 渐变至 `#BFE4E9`） |
| Page blue | `#2478DF` | 蓝色主页面中点（`#3B97F6` → `#175FC0`） |
| Back-page green | `#12B981` | 折角下露出的背页（主稿 `#23C48D` → `#079A72`） |
| Fold amber | `#FF9F2E` | 橙黄折面（主稿 `#FFD166` → `#FFAB2E` → `#FF7438`） |
| Rear-sheet mint | `#84D8BE` | 右下错落的后页 |
| Deep teal | `#006A6A` | 与应用 MD3 默认种子色保持连续，只作界面种子，不在新图标里抢主体 |

## 4. 资产落点

| 用途 | 文件 |
|---|---|
| 可编辑主稿 | `docs/assets/brand/eidoria-icon.svg` |
| 品牌 PNG（1024） | `docs/assets/brand/eidoria-icon.png` |
| UI 内嵌 PNG（512） | `src/MdExplorer.UI/Assets/eidoria.png` |
| 窗口/桌面 ICO | `src/MdExplorer.UI/Assets/eidoria.ico`（16/20/24/32/40/48/64/128/256） |
| Android adaptive | `src/MdExplorer.Android/Resources/mipmap-anydpi-v26/ic_launcher.xml` + `drawable/ic_launcher_foreground.xml` |
| Android 旧矢量兜底 | `src/MdExplorer.Android/Resources/drawable/icon.xml` |

修改主稿后须重新导出 PNG/ICO，并同步检查 Android adaptive 的简化前景、圆形裁切与小尺寸识别。

## 5. 使用规则

- 不拉伸、不旋转、不给图标加描边、投影或外发光，不在图标内叠加产品文字。
- 不把蓝页、绿背页或橙折角拆开冒充分享、同步等功能图标；界面功能图标继续使用 Material Symbols。
- 图标放在浅色背景上时保留自身薄荷底，不额外垫白色卡片。
- 品牌名在窗口、启动器等窄空间只显示 `Eidoria`；抽屉、README 与关于页可同时显示“万象屿”。

## 6. 改名迁移边界（v0.6）

本轮仍只改**产品品牌层**：窗口标题、抽屉品牌、设置页名称、Android 应用名、图标、桌面产品元数据与文档标题。

以下内部标识暂不改，避免升级与数据迁移风险：

- GitHub 仓库名、解决方案/项目目录与 C# 命名空间：仍为 `MdExplorer`；
- Android `ApplicationId=dev.mdexplorer.app`：保持应用更新身份连续；
- 桌面 `AssemblyName=MdExplorer` 与本地数据目录 `MdExplorer`：保持已有设置、收藏与快捷方式可继续使用；
- 应用内拖放私有数据格式仍为 `x-mdexplorer-paths`。

若 1.0 前进行仓库级改名，应单独立项处理重定向、数据目录迁移与发布兼容。
