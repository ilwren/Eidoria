# Eidoria（万象屿）

<img src="docs/assets/brand/eidoria-icon.png" alt="Eidoria 图标：蓝色竖向页片左上角翻起，露出绿色背页与橙黄色折面" width="96" />

基于 **Avalonia 12 + .NET 10** 的跨平台（Windows / Linux / Android）**本地**文件资源管理器，采用 **Material Design 3**（`Md3.Avalonia`）设计语言，按窗口尺寸类做真响应式（Adaptive）布局：底部导航栏（Compact）→ 导轨（Medium）→ 永久抽屉 + 预览侧栏（Expanded+）。核心层平台无关，平台能力经接口适配；许可证 Apache-2.0（见 `LICENSE`）。

> **状态：v0.41**——浏览/搜索/预览（图片、文本、**Markdown 渲染、代码高亮、PDF 直接预览、Office 只读渲染**、ZIP 条目）/复制移动删除（含回收站语义）/ZIP/收藏/设置已可用；v0.2 新增文件夹外部变更自动刷新、文件夹大小、校验和与批量重命名；**v0.3 新增 Android SAF 授权文件夹浏览**；**v0.4 新增详细信息视图**；**v0.5 新增拖放与系统剪贴板互通**；**v0.6 新增浏览器标签页与 PDF 分页预览，并将产品品牌定为 Eidoria（万象屿）**（名称与图标见 `docs/BRAND.md`；仓库、命名空间、Android ApplicationId 与数据目录仍保留内部代号 MdExplorer 以兼容既有数据）；**v0.7 新增串行任务中心、可编辑地址栏、最近访问与标签页恢复**；**v0.8 按概念稿重塑视觉层级：柔和底板承载白色工作区与预览浮动卡，标签页改为与工作区同面相连的浏览器式页签，路径胶囊、类型色图标井、圆角文件卡与精确 `MdList` 选中态统一收敛，Compact 将标签轨与面包屑折叠为当前目录**；**v0.9 换装极简新 Logo（深湖拱门·金日屿浪），三栏加入自研 Material 分隔条（拖动/键盘调整并持久化宽度，原型将回流 Md3.Avalonia），目录切换/页面与侧栏补齐入场与点击动效，插件脚本完成 Lua 优先评估（MoonSharp spike、Python 走进程外）**；**v0.10 新增按文件夹记忆视图与排序（可关、可清除、最多 200 个）和应用内“减少动态效果”，Material 分隔条补齐双击/Home 复位、16px 热区与 `:reduce-motion` 状态，设置页版本改为动态读取**；**v0.11 将整行搜索改成迷你搜索岛 + 悬浮搜索面板（Ctrl+F/K、回车递归、系统返回先收面板），预览拆为“预览 / 属性”双页签且 Compact/Medium 改用 Md3.Extra `MdFloatingWindow` 悬浮预览，全仓显式溢出区统一 `MdScrollViewer`，路径胶囊点空白编辑、点分段跳转，溢出菜单改为桌面三列分组限高，并以概念稿的蓝色折页重绘图标**；**v0.12 对照 `ai-v3` 概念图收敛真实界面：文件行/卡只保留一个紧凑元数据，桌面工具栏、搜索岛、状态区、任务与回收站优先图标+数值，Compact 顶栏新增带计数折叠页签入口，预览改为贴顶舞台与图标事实条，卡片/选中态/Rail 指示器统一到 MD3 tonal 表面与形状阶梯**；**v0.13 新增本地文件名索引与搜索中心：用户显式加入索引根后可跨根按名称/类型查询，页面同屏管理条目数、体积、更新时间、启停、重建与清除，浏览器递归搜索在快照覆盖时走索引、运行期增删改名使快照失效后回落实时扫描（正文/内容索引仍未实现）**；**v0.14 新增只读存储洞察：分析本地文件夹的总量、类型分布、一级子文件夹占用与大文件，并以同尺寸预筛 + SHA-256 分组复核精确重复文件（只定位、不自动清理；Android SAF 暂不分析）**；**v0.15 新增 ZIP 家族压缩包工作台：把本地 `.zip/.jar/.apk` 当作只读虚拟文件夹逐层浏览，条目同时显示原始/压缩大小，文本与图片可在 16 MiB 上限内免整包解压预览，并把解压所选与解压全部分开，目标、保留结构、同名处理（默认两者保留）和完成打开都在执行前说清；7z/RAR/Tar、包内编辑与 SAF 压缩包仍未实现**；**v0.16 新增 Android 存储授权管理与应用内回收站：SAF 根按“已保存 / 仅本次会话 / 需要重新授权”如实呈现，失效书签不再静默消失，移除只清 Eidoria 入口而不删文件；Android 删除先把应用专属目录与 SAF 项目复制到应用数据目录（默认 1.0 GB、30 天自动清理），配额拒绝或还原冲突时保留原项目/回收站副本，桌面仍使用系统回收站；仍坚持 SAF 优先，不申请“所有文件访问”**；**v0.17 新增逐项冲突确认与任务详情：复制/移动先在零写入的规划阶段逐个比较来源与目标（可得时显示缩略图、大小、修改时间与 SHA-256），默认保留两者，只有显式勾选才把策略应用到其余冲突；任务中心新增等待确认状态、完成/跳过/失败/产物计数、错误与产物定位、开始/结束时间，并可按原策略只重试传输中的失败项，Compact 任务中心改为贴底 Sheet**；**v0.18 新增 ExtraLarge 双窗格第一切片：主窗格 A 与对照窗格 B 使用独立浏览器会话（路径、历史、选集与滚动互不串扰），活动侧以主色描边与徽标标识并驱动预览和键盘，支持分隔条调宽、显式复制/移动到对照窗格、跨窗格拖放（默认移动、Ctrl 复制）以及 `Ctrl+Shift+D` 开关与 `F6` 切换活动窗格；B 的路径与宽度会持久化，宽度跌出 ExtraLarge 时 B 收为 A 的后台标签而会话仍保留。第一切片中全局文件标签、悬浮搜索与可编辑地址栏仍只属于 A，B 的详细信息视图暂回落列表**；**v0.19 修复文件 item 三个点打开未挂载共享上下文菜单造成的崩溃，文件标签支持指针拖拽重排（拖动中让位、释放提交、同会话对象与活动标签不变、顺序跨启动恢复），桌面版改为无原生标题栏的 48px 顶部 Chrome：Expanded+ 标签位于整个窗口顶缘并与最小化/最大化或还原/关闭同行，抽屉品牌在左侧对齐，空白区拖窗、双击切换最大化、8 向边缘缩放，窄桌面窗口及选择模式也保留窗口按钮，Android 单视图不显示桌面窗口控件**；**v0.20 新增独立文档工作台第一切片：本地文本/代码/Markdown 与明确文本型数据文件可在 2 MiB 内完整读取，默认只读，显式进入编辑后才可修改、查找、撤销/重做，Markdown 可切源稿/渲染，JSON/XML 可安全格式化；保存经任务中心串行执行，保留 Unicode 编码、BOM 与主导换行，以 SHA-256 快照在同目录临时文件刷盘后再次复核并原子替换，外部修改时只能重载或明确覆盖，只读、符号链接、二进制、非法编码与超限内容拒绝编辑。PDF 工作台提供单页阅读、懒加载缩略图轨、50%–300% 缩放重渲染、90° 旋转、页码跳转与仅驻内存的密码解锁，Compact 收起缩略图轨和次级工具动作；PDF 连续滚动、目录/书签、文档内搜索、Android SAF 文本写回与完整 IDE 能力仍未实现**；**v0.21 恢复 Windows 无边框窗口的原生缩放动画、接入操作系统通知与 NodifyM 自动化节点编辑器：Windows 改以 `Full` 原生装饰保留 `WS_CAPTION`/DWM 动画，并用空白 `WindowDrawnDecorations` 主题隐藏框架自绘标题与按钮（Linux 仍用 `BorderOnly`）；`Md3.Avalonia.Notifications` 经自有 `ISystemNotificationService` 隔离，设置页默认关闭的长任务通知只提醒失败或运行至少 3 秒的任务，并提供测试发送，Windows 首次发送时才注册当前用户 AppUserModelID 快捷方式、Android 首次发送时才申请 `POST_NOTIFICATIONS`。新增“自动化”第一切片只执行一条可持久化单链（当前本地文件夹顶层文件 → 类型/名称筛选 → 复制或移动到一级子文件夹 → 可选系统通知），无匹配不建空夹、同名始终保留两份、运行仍走任务中心，暂不支持分支/循环/递归/SAF；两个 Md3 包 `Md3.Avalonia.NodifyM 0.5.0-preview.2` 与 `Md3.Avalonia.Notifications 0.5.0-preview.2`（Apache-2.0）声明 Trim/AOT 兼容，上游 `NodifyM.Avalonia 12.0.0`（MIT）声明 AOT 兼容，且本仓 Linux Native AOT 与 Android trimming/AOT 发布已实测通过，但真实 Windows 11 动画/Toast 与 Android 通知呈现仍须实机验收**；**v0.22 推进文档工作台第二切片：PDF 新增可与单页模式切换的连续阅读，连续页由虚拟化 `MdList` 在进入视口时按当前缩放/旋转逐页渲染，滚动位置会同步当前页、缩略图轨与页码工具，整页位图缓存受 10 页与约 96 MiB 估算预算约束；桌面主窗口关闭时若文本仍有未保存更改会先请求明确放弃，正在保存时则阻止关闭，避免自绘关闭按钮或系统关闭路径静默丢稿。PDF 目录/书签、文本层搜索与命中高亮仍未实现（SAF 文本安全写回在 v0.30 补齐）**；**v0.23 接入 MIT 许可的 `OfficeViewer.Avalonia 0.1.6`，为 `.docx/.pptx/.xlsx` 提供快速预览与文档工作台只读渲染（DOCX/PPTX 连续滚动、XLSX 宿主切表），宿主先以文件服务有界读入并预检 ZIP 展开量、XML/媒体规模、加密/宏部件与工作表网格范围，超限、旧二进制 Office 与不受支持的复杂对象明确回落系统应用；PDF 仍以 PDFium 为主引擎，仅在 Android API <31 或原生组件不可用时改走 OfficeViewer 的常见 PDF 子集兼容回落。Office 定位是常见 OOXML 的兼容子集而非完整办公套件，浮动对象、动画、复杂图表/透视表与公式重算可能缺失或与原版有别**；**v0.24 将 PDF 改为直接控件优先：快速预览与文档工作台默认把有界读入的原始 PDF 交给 OfficeViewer 的 Avalonia 原生 `PdfViewer` 连续呈现，适配层以页数、上游错误横幅与 10 秒超时识别解析失败；加密/密码、明文 `/Encrypt`、超直接上限（快速预览桌面 32 MiB/Android 16 MiB，工作台 64/32 MiB）、直接控件失败或用户手动选择时才进入 PDFtoImage/PDFium 光栅兼容视图，后者仍保留密码、缩略图、单页/连续阅读、缩放与旋转，并可在适用时切回直接预览；Android API <31 不触碰 PDFium，直接控件失败时明确回落系统应用。官方 Avalonia `PdfViewer` 功能更完整但需要 Pro/Enterprise 商业许可，故本仓不把它作为 Apache-2.0 默认依赖**；**v0.25 为核心文件面接入 Md3 下拉刷新与渐进分页：A 的列表/网格/详细信息与 B 的列表/网格分别使用 `MdRefreshIndicator`，手动刷新与 F5/Ctrl+R 共用刷新状态，文件夹监视仍静默软刷新；目录仍完整枚举并全局排序/过滤，但首屏只物化 120 行，滚动接近底部或点击“加载更多”时再按页创建行 VM、控件与缩略图，底部明确已显示/总数。全选、文件定位、标签选集恢复与软刷新选集会展开到目标项，搜索角标和状态使用完整总数，因此该分页只延后 UI 物化、不改变文件操作语义，也不是磁盘分页 I/O；**v0.26 扩展设置并固定抽屉品牌区：浏览页新增每页项目数（60/120/240）、近底自动加载开关、图片缩略图开关、舒适/紧凑列表密度、移入回收站前确认与下拉刷新触发距离（72/96 DIP），“启动与最近”新增标签会话恢复开关与最近记录条数（12/24/48）；这些值全部落盘、夹到支持区间，并即时作用于 A/B 两窗格与主页最近访问。Large 常驻抽屉与 Compact 模态抽屉的品牌 Logo 已从滚动区移出、固定在顶部，只有导航列表随滚动移动**；**v0.27 补齐对照窗格详细信息与独立呈现偏好：B 窗格新增与 A 同列的详细信息视图（名称/大小/类型/修改时间、列头排序、Ctrl 多选、拖放、右键菜单与近底续页全部复用既有管线），并修正在 B 切到详细信息时列表仍留在视觉树里抢占命中区的问题；B 的视图与排序改存 `DualPaneViewMode/DualPaneSortField/DualPaneSortDirection`，不再覆盖 A 的全局默认与按文件夹记忆，启动时各自恢复，非法值回落跟随全局。B 的 560dp 默认宽度下，跨窗格传输改为图标按钮（完整语义在 ToolTip 与无障碍名），详细信息列宽收敛为名称\*/96、大小 82、类型 76、修改时间 146/136，窄栏下时间不再被裁切，更窄时横向滚动而不是静默省略**；**v0.28 给对照窗格补上自己的可编辑地址栏：点路径胶囊空白进入编辑、Enter 提交、Esc 取消、错误就地提示，`Ctrl+L` 编辑“活动窗格”的路径而不是永远编辑 A，B 的输入框与面包屑按 B 自己的编辑态互换，A 的路径胶囊完全不受影响**；**v0.29 给对照窗格补上自己的搜索：B 的标题行新增带结果角标的迷你搜索岛，面板只压在 B 的文件区上（轻遮罩、不挡工具栏），“递归 / 当前列表 / 清除 / 退出”与 A 完全同语义但只作用于 B 会话；`Ctrl+F/K` 打开活动窗格的面板，返回键先收 B 的面板再谈后退。设置页同时新增三项可调项：滚动自动续页距离（160/320/480 DIP）、下拉刷新手势总开关（关掉后仍保留工具栏刷新与 F5/Ctrl+R）、递归与索引搜索的结果上限（1000/2000/5000，达到上限会如实截断），以及 Android 应用内回收站的配额（500 MB/1 GB/2 GB）与保留期（7/30/90 天）——后两项只有应用内回收站显示，桌面系统回收站不会给出假选项**；**v0.30 补上对照窗格最后两处不对称并打开两个新缺口：B 新增自己的标签轨（只在第二个标签出现时展开，点标签只切 B 的活动标签、不写 A 的会话）与标题行溢出菜单（编辑/复制路径、新建与关闭标签、交换两窗格目录、刷新、关闭对照窗格）；Android 授权文件夹中的文本现在可以写回——新 `IFileSystemService.OpenWriteAsync` 端口 + 共用 `TextFileCodec` + `StorageTextFileService`，顺序是编码、指纹复核、就地覆盖、写后读回比对、不一致时尽力恢复原内容并如实报错，**绝不宣称原子**，本地路径仍走临时文件 + `File.Replace`；内嵌播放先立平台无关端口 `IMediaPlaybackService`，Android 用系统 `MediaPlayer` 提供**音频**内嵌播放（视频画面与桌面解码后端仍未接入，桌面在无后端时明确自述而不静默降级）**。**v0.31 把内嵌播放改成「直接用每端系统自带的媒体栈」：Android 系统 `MediaPlayer`、Windows Media Foundation（WinRT `MediaPlayer`）、Linux 发行版自带 GStreamer `playbin`，三端都不捆绑 FFmpeg/libVLC/libmpv，包体零增长、无 LGPL 源码义务；代价是「能播什么」由系统/设备/发行版决定，因此新增播放前能力探测（`IMediaProbe`）与画面交付方式契约（`RendererKind`），对「未知 / 系统不支持该容器 / 含视频轨」分别给出事实文案，而不是按扩展名猜**。标签页各自保存路径、前进/后退栈、过滤词与选集，并可跨启动恢复；PDF 预览默认使用直接控件，MIT 许可的 PDFtoImage/PDFium 只作光栅兼容路径。**v0.32 把 Android 的视频画面接进应用内：`MediaPlayer` 解码后的画面交给宿主托管的 `TextureView`（Avalonia 的 `NativeControlHost` 在 Android 上已实现 `INativeControlHostImpl`），新增 `IMediaVideoRenderer` 端口与 `MediaSurfaceHost` 容器，并把「视频可播」收紧为「能力位 + 画面通道」双条件，只有能力位没有画面通道的后端一律算不能播，不给「有声无画」的假播放**。**v0.33 修两处用户可见问题并继续推进视频画面：①图片点开时悬浮预览不再展开成空壳——浮窗只显示 `Preview`，而 `Preview` 由选集事件驱动，重复点同一项、预览被清空或窗格切换时选集不会再次喂内容；现在 `OnFileOpened` 显式核对内容来源，换项即展开被收起的浮窗并显式复位展开高度（`MdFloatingWindow` 在“先收起再换文件”的时序里会丢掉展开高度，只剩属性正确），`LoadAsync` 也在每处 await 之后校验代次，旧代次不会再把新选中项清空；②分页改为「滚到底自动补齐」：`BeginBackgroundFill` 每批 24 条、每批让出 UI 线程地把剩余条目物化完，`EnsureVisibleRange` 先保证一屏放得下的行就绪，页脚转圈区分手动加载与后台补齐；③新增宿主帧呈现面（`IMediaFrameSink` + `MediaFrameSurface`），Linux 的 GStreamer 用 `appsink` 取 BGRA 帧推给 Avalonia，Wayland 下也能出画面，缺 `appsink`/`videoconvert`/`libgstapp` 时如实降级为系统应用打开，Windows 视频画面仍需 HWND 或帧服务宿主、本版交系统应用打开**。**v0.34 把 Windows 的视频画面接上：系统 `MediaPlayer` 继续用 Media Foundation 解码音频，画面改由 `MediaComposition` 按播放位置取缩略帧（480×270 / 约 12 fps）解成 BGRA，经与 Linux appsink 同一条 `IMediaFrameSink` 通道交给 Avalonia 自己画，三端从此都有画面；探测仍是「视频容器真的建一次取帧组合」，建不出来就如实拒绝（只有音轨的 `.mp4` 也不给「有声无画」），取帧路线帧率低于原生播放、HEVC 仍需系统扩展，完整播放依旧提供「用系统应用打开」。同一版修掉 Large 下预览右缘的硬裁：媒体后端名改由 `Grid` 的 `*` 列约束并换行/省略，文档与压缩包工作台按钮去掉硬 `MinWidth` 并给标签加省略，截图 76 补了「右边缘不越过父级」的断言**。**v0.35 补上 OpenDocument 预览：`.odt/.ods/.odp`（含 `.ott/.ots/.otp` 模板与 `.fodt/.fods/.fodp` 平面 XML）改由内置只读器直接解析 ZIP/XML，把标题层级、段落、列表、表格单元格与幻灯片顺序还原成 Markdown 交给既有渲染器——不引入 LibreOffice、不新增任何依赖，`OfficeViewer` 仍只负责 OOXML；有界读取、禁 DTD 与输出字数预算保证压缩炸弹或超大表格不会把界面卡死，不还原版式这件事写在预览开头，并保留「用系统应用打开」**。**KDBX（KeePass 密码库）预览的 GPL 绕行方案已评估**：KeePass/KeePassLib（GPL-2.1+）、KeePassXC（GPL-2/3）、ModernKeePassLib（GPL-3）一律不用，可行路线是 MIT 的 `Pidamg.KeePass.Kdbx`（0.1.1，仅依赖 MIT 的 BouncyCastle）或按公开格式规范用 BouncyCastle 原语自写只读解析，排在下一版。**插件系统与本地 AI 两条轨道已完成评估、仍排在核心完善之后**。**v0.36 收掉两处遗留问题：①100+ 页 PDF 的快速预览不再把提交内存顶到 ~1 GB——直接控件先按 PDFium 数出的页数过闸门（>24 页改走虚拟化光栅），关闭浮窗/预览栏时 `ReleaseHeavyContent()` 抬代次、清位图（旧图在渲染落地后异步释放）、清 PDF 字节与直接控件，重开时按当前选集（选集被清空时按上一次预览的文件）自动重载，文本类内容仍保留；②`.kdbx` 密码库可以只读解锁——按路线 B 自写 KDBX 3.1/4.x 解析（BouncyCastle 原语，AES-KDF / Argon2d / Argon2id、AES-256-CBC、ChaCha20/Salsa20 内层流、HMAC 分块与头部校验），只展示分组与条目的标题/用户名/URL/备注，密码字段默认遮罩、可显式揭示，主密码只驻内存、关闭即清、不进日志不落盘，失败文案不泄露库内容；密钥文件与 KDBX 2 明确不支持。****视频默认交给系统播放器**：预览卡给「系统播放器 / 仍要内嵌观看」两个显式入口，设置新增「视频默认内嵌出画面」「选中媒体后自动播放」「显示媒体后端诊断」三项。**v0.37 按维护者要求把 PDF 之后的工作让位给本地 AI，先做「模型分发 + 状态」这一层**：仓库新增 `scripts/fetch-ai-models.sh`（Linux/macOS/WSL）与 `scripts/fetch-ai-models.ps1`（Windows），按 `scripts/ai-models.manifest`（纯文本、脚本与应用共用同一份）下载中文嵌入 BGE small zh v1.5（MIT，约 26 MB）与 all-MiniLM-L6-v2（Apache-2.0，约 23 MB），无参数仅下载约 49 MB 默认轻量包；可选多模态模型包括 Whisper tiny 多语语音转写（ONNX 导出 Apache-2.0，约 57 MB）、YAMNet 声音事件分类（Apache-2.0，约 16 MB）与 SmolVLM-256M 图像问答（Apache-2.0，ONNX q4f16 约 194 MB，英文优先），支持 `--only <id>` 单独下载、`--all` 全部约 316 MB。支持 `.part` 断点续传、SHA-256 首次记录与后续校验、`HF_ENDPOINT` 镜像、bash `--list/--only/--all/--verify/--dry-run/--clean` 与 PowerShell `-List/-Only/-All/-Verify/-DryRun/-Clean`；权重不进仓库（`models/` 已 gitignore）。设置页新增「本地 AI」卡：总开关（默认关）、逐模型状态（已就绪 / 下载不完整缺几个文件 / 未安装，含实际占用与许可）、可复制的下载命令、一键打开模型目录。**v0.38 接入端侧 AI 语义文件搜索**：搜索中心可切换关键词/自然语言语义模式，按用户显式添加的索引根建立本地向量快照；使用 ONNX Runtime + BERT WordPiece，将文件名、父目录与限量纯文本/Markdown 摘要编码后做 masked mean pooling、L2 归一化和余弦排序。索引仅保存元数据与量化向量、不保存正文；打开设置中的本地 AI 并安装模型后才允许运行，清除索引只影响应用本地快照。真实下载权重后的端到端质量、性能与设备验收仍待验证（见 `docs/LOCAL-AI.md`）。**v0.39 补齐内嵌媒体播放的第二条后端**：维护者实测 Windows 系统栈的取帧路线卡顿明显，因此新增可选的 **libmpv** 后端并与系统解码栈并存——平台抽象新增 `MediaBackendPreference`（Auto / SystemApi / LibMpv）与 `IMediaBackendSelector`，`MediaBackendSelector` 按偏好与实际能力位裁决（**能力位为假的后端一律不算可用**），显式点名却用不上时写「已回退 + 原因」，Auto 只报「谁没被用上、为什么」。桌面新增 `MpvMediaPlaybackService`：只引用不含 UI 依赖的 `HanumanInstitute.LibMpv` 核心包，自己驱动 `SoftwareRender` 把 BGRA 帧推给宿主（复用已有 `IMediaFrameSink` 通道，不需要原生控件或 GL 互操作），配独立渲染线程、500 ms 属性轮询与 5 秒打开看门狗；`MpvNativeLibrary` 先用 `NativeLibrary.TryLoad` 探测，库缺失时连上下文都不创建，避免布局期闪退。设置页「媒体」新增**自动 / 系统解码栈 / libmpv** 三选一与实时裁决摘要。本仓仍只带 MIT 托管包，libmpv 原生库由用户自备（LGPL-only，放程序目录或 `MDEXPLORER_LIBMPV_ROOT` 指路），应用不打包不下载不联网。**刻意不用 `LibMpv.Avalonia`**：它依赖 `ReactiveUI.Avalonia`（禁用清单）且该依赖在 Linux 上触发 NuGet 签名校验失败 NU3012。两条路线的真机流畅度对比、硬件解码与 Native AOT 下的表现仍待验收（见 `docs/LIBMPV-SPIKE.md`）。**v0.40 缩略图三档 + 磁盘缓存 + 负缓存**：图片缩略图不再每次进目录都重解码——新增 `IThumbnailCache` 端口与 `FileThumbnailCache`，键 = 路径 + 档位 + 文件大小 + 修改时间，缓存落在应用数据目录 `thumbnails/`（上限 192 MB，按最后写入时间自动清扫），解码失败写 `.fail` 负缓存标记并在 24 h 后失效（文件一改立即失效）；设置页新增 **小 128 / 中 192 / 大 288** 三档（默认中档，与旧观感一致）、缓存占用摘要与「清空」。翻回看过的目录直接命中位图，不再与解码并发额度抢队。内存 LRU、视频海报/PDF 首页缩略图与平台原生缩略图仍未做。**v0.41 全文内容索引（FTS5）**：搜索中心新增「全文」模式，可跨索引根在文件正文里按词查找——默认关闭，用户显式建立后才收录（只收白名单文本扩展名、单文件 ≤ 512 KB，跳过隐藏目录与 `node_modules`/`bin`/`obj`，不穿符号链接），索引落在应用数据目录 `content-index.db`，与文件名索引、AI 向量索引各自独立、可单独清空。中文两字查询用重叠二元组展开（`trigram` 要求 ≥3 字符，不适配中文），摘要仍显示原文上下文；重建经任务中心执行、可取消，未变文件跳过，整次重建一个事务（取消即回滚）。FTS5 不可用时降级为「未建立」并如实报状态；真增量、PDF/Office 正文抽取与命中高亮仍未做。

## 效果（CI 无头渲染实拍，归档于 `docs/assets/screenshots/`，含 Markdown/代码/批量重命名/详细信息视图/拖放提示/标签页/PDF/地址栏/任务中心/精修主页/悬浮预览/搜索岛/桌面溢出菜单/搜索中心/存储洞察/压缩包工作台/Android 授权管理/应用内回收站/逐项冲突确认/任务详情/Compact 任务 Sheet/ExtraLarge 双窗格/文档工作台（Large 文本、Large/Compact PDF 直接预览、单页/连续 Large PDF 兼容视图、单页/连续 Compact PDF 兼容视图、Large PDF 光栅快速预览与未保存关闭守卫）、Office 只读预览（Large DOCX/PPTX/XLSX 工作台、Compact PPTX 与 Large XLSX 快速预览）、自动化节点编辑器（Large 浅色、Compact、Large 深色）、文件分页/下拉刷新（Large 加载更多、Compact 正在刷新）、设置页（Large 外观/浏览分组）、ExtraLarge 双窗格详细信息（B 独立排序与选集镜像）、双窗格地址编辑与双窗格搜索（B 面板只覆盖文件区）、B 标签轨与目录交换、内嵌音频播放（Large 播放中与 Compact 悬浮预览）、媒体探测拒绝态（系统解码器不认这个文件）、浮窗收起后换图重新展开、推帧视频画面（Linux appsink / Windows 取帧路线）、OpenDocument 只读预览（Large ODT、Compact ODS）共 69 张）

<img src="docs/assets/screenshots/17-large-tabs.png" alt="Large 1440：无边框窗口顶部双标签页与窗口按钮 + 白色工作区 + 网格卡片 + 预览浮动卡" width="720" />

<img src="docs/assets/screenshots/01-compact-390x844.png" alt="Compact 390：底部导航栏 + 列表 + FAB" width="240" /><img src="docs/assets/screenshots/02-compact-home.png" alt="Compact 主页：存储卡与分类" width="240" /><img src="docs/assets/screenshots/03-medium-768x1024.png" alt="Medium 768：导轨 + 网格" width="300" />

<img src="docs/assets/screenshots/22-compact-floating-preview.png" alt="Compact 悬浮预览：可拖窗内含预览与属性双页签" width="240" /><img src="docs/assets/screenshots/23-large-search-island.png" alt="Large 悬浮搜索面板：迷你搜索岛展开后的搜索控制面" width="520" />

<img src="docs/assets/screenshots/26-compact-search-center.png" alt="Compact 搜索中心：跨索引根查询与索引管理上下分工" width="240" /><img src="docs/assets/screenshots/25-large-search-center.png" alt="Large 搜索中心：跨索引根结果与索引健康同屏" width="520" />

<img src="docs/assets/screenshots/28-compact-storage-insights.png" alt="Compact 存储洞察：占用排行与类型分布单列滚动" width="240" /><img src="docs/assets/screenshots/27-large-storage-insights.png" alt="Large 存储洞察：卷、子文件夹、类型与大文件同屏" width="520" />

<img src="docs/assets/screenshots/30-compact-archive-workbench.png" alt="Compact 压缩包工作台：虚拟目录条目与详情上下分工" width="240" /><img src="docs/assets/screenshots/29-large-archive-workbench.png" alt="Large 压缩包工作台：虚拟目录、条目预览与解压选项同屏" width="520" />

<img src="docs/assets/screenshots/31-compact-android-access.png" alt="Compact Android 授权管理：已保存、仅本次会话与需重新授权三种状态" width="240" /><img src="docs/assets/screenshots/32-compact-android-app-trash.png" alt="Compact 应用内回收站：配额、保留期、还原与永久删除" width="240" />

<img src="docs/assets/screenshots/34-compact-conflict-dialog.png" alt="Compact 逐项冲突确认：来源与目标的缩略图、大小、修改时间、校验和与三项策略" width="240" /><img src="docs/assets/screenshots/33-large-conflict-dialog.png" alt="Large 逐项冲突确认：来源与目标并排比较，默认保留两者" width="520" />

<img src="docs/assets/screenshots/36-compact-task-sheet.png" alt="Compact 任务中心底部 Sheet：失败原因、计数、产物与只重试失败项" width="240" /><img src="docs/assets/screenshots/35-large-task-details.png" alt="Large 任务详情：完成、跳过、失败与产物计数及失败重试" width="520" />

<img src="docs/assets/screenshots/37-xlarge-dual-pane.png" alt="ExtraLarge 双窗格：A/B 独立目录与选集、活动窗格描边、跨窗格复制与移动操作" width="720" />

<img src="docs/assets/screenshots/59-xlarge-dual-pane-details.png" alt="ExtraLarge 双窗格详细信息：B 窗格与 A 同列并可独立按大小降序，选集与 A 镜像" width="720" />

<img src="docs/assets/screenshots/60-xlarge-dual-pane-address-edit.png" alt="ExtraLarge 双窗格地址编辑：B 的路径胶囊换成输入框并就地提示错误，A 仍保持面包屑" width="720" />

<img src="docs/assets/screenshots/61-xlarge-dual-pane-search.png" alt="ExtraLarge 双窗格搜索：B 标题行的迷你搜索岛展开后在窗格内筛选，A 的列表与搜索状态不受影响" width="720" />

<img src="docs/assets/screenshots/73-xlarge-dual-pane-tabs.png" alt="ExtraLarge 双窗格标签：B 的两个标签只占本窗格头部，交换目录后 A/B 当前目录互换而各自历史保留" width="720" />

<img src="docs/assets/screenshots/76-large-video-frame-push.png" alt="Large 推帧视频画面：后端推 BGRA 帧、Avalonia 自己画（Linux appsink 与 Windows 取帧共用同一条通道）" width="520" /><img src="docs/assets/screenshots/75-compact-floating-preview-expand.png" alt="Compact 浮窗收起后换图：内容重新展开并换成新图" width="240" />
<img src="docs/assets/screenshots/74-large-media-probe-rejected.png" alt="Large 媒体探测拒绝：系统解码器不支持时只给事实、原因与系统打开，不出现播放控件" width="520" /><img src="docs/assets/screenshots/71-large-audio-playback.png" alt="Large 内嵌音频播放：预览页媒体面板的播放、±10 秒、停止、进度与后端诊断" width="520" /><img src="docs/assets/screenshots/72-compact-audio-playback.png" alt="Compact 悬浮预览中的内嵌音频播放面板" width="240" />

<img src="docs/assets/screenshots/40-compact-document-editor.png" alt="Compact 文档工作台：Markdown 源稿轻编辑与紧凑文件事实" width="240" /><img src="docs/assets/screenshots/38-large-document-editor.png" alt="Large 文档工作台：代码轻编辑、保存与有界差异摘要" width="520" />

<img src="docs/assets/screenshots/54-compact-pdf-direct-workbench.png" alt="Compact PDF 直接预览工作台：Avalonia 原生控件连续呈现两页与兼容视图入口" width="240" /><img src="docs/assets/screenshots/53-large-pdf-direct-workbench.png" alt="Large PDF 直接预览工作台：OfficeViewer Avalonia 原生控件、页数与兼容视图入口" width="520" />

<img src="docs/assets/screenshots/41-compact-pdf-workbench.png" alt="Compact PDF 兼容视图工作台：无缩略图轨的单页阅读、页码与缩放" width="240" /><img src="docs/assets/screenshots/39-large-pdf-workbench.png" alt="Large PDF 兼容视图工作台：缩略图轨、125% 缩放与第二页阅读" width="520" />

<img src="docs/assets/screenshots/55-large-pdf-raster-preview.png" alt="Large PDF 光栅快速预览：从直接控件切换到 PDFium 后按页浏览第二页" width="720" />

<img src="docs/assets/screenshots/43-compact-automation.png" alt="Compact 自动化：窄屏按可读缩放平移的节点画布" width="240" /><img src="docs/assets/screenshots/42-large-automation.png" alt="Large 自动化：当前文件夹到类型筛选、传输与系统通知的单链节点图" width="520" />

<img src="docs/assets/screenshots/44-large-automation-dark.png" alt="Large 自动化深色：同一单链节点图与节点检查器" width="720" />

<img src="docs/assets/screenshots/46-compact-pdf-continuous.png" alt="Compact PDF 连续阅读：整页纸面、连续模式与页码同步" width="240" /><img src="docs/assets/screenshots/45-large-pdf-continuous.png" alt="Large PDF 连续阅读：缩略图轨、50% 纸面与连续滚动" width="520" />

<img src="docs/assets/screenshots/47-large-unsaved-close-dialog.png" alt="关闭未保存文档确认：脏文本、差异摘要与放弃并关闭动作" width="720" />

<img src="docs/assets/screenshots/51-compact-office-pptx.png" alt="Compact Office 工作台：两张 PPTX 幻灯片按整页宽度连续呈现" width="240" /><img src="docs/assets/screenshots/49-large-office-pptx.png" alt="Large Office 工作台：PPTX 标题、形状与文字只读渲染" width="520" />

<img src="docs/assets/screenshots/48-large-office-docx.png" alt="Large Office 工作台：DOCX 标题、正文、项目符号与表格只读渲染" width="720" />

<img src="docs/assets/screenshots/50-large-office-xlsx.png" alt="Large Office 工作台：XLSX 第二张工作表的行列标题与带样式单元格" width="520" /><img src="docs/assets/screenshots/52-large-office-preview-xlsx.png" alt="Large Office 快速预览：XLSX 第二张工作表与切表状态" width="520" />

<img src="docs/assets/screenshots/57-compact-browser-pull-refresh.png" alt="Compact 文件下拉刷新：Md3 正在刷新浮层与已显示 120 / 150 项加载更多页脚" width="240" /><img src="docs/assets/screenshots/56-large-browser-load-more.png" alt="Large 大文件夹渐进分页：首屏 120 项与加载更多页脚" width="520" />
<img src="docs/assets/screenshots/58-large-settings.png" alt="Large 设置：固定的抽屉品牌区、分段选择（主题/密度/主题色）与浏览开关" width="720" />

## 下一阶段概念（非实拍）

概念设计 v3 已按 Material 3 Skill 审计收敛文字与标签页，并把当前基线做成深浅成对稿，并预演搜索索引、双窗格、存储洞察、压缩包工作台、编辑/PDF、媒体、插件/Lua 与本地 AI 的界面样式。完整图集、优先级与验收规则见 [docs/CONCEPT-DESIGN.md](docs/CONCEPT-DESIGN.md)；规划图不代表功能已经实现。

<img src="docs/assets/mockups/ai-v3/01-large-browser-light.png" alt="Eidoria AI 直接生成概念图：Large 浅色浏览器" width="720" />

## 构建与运行

前置：.NET 10 SDK（`global.json`）；Android 另需 `dotnet workload install android` + JDK 17；Linux Native AOT 另需 `clang`、`zlib1g-dev`。

```bash
# 桌面调试（Windows/Linux 通用）
dotnet run --project src/MdExplorer.Desktop

# 测试（含文件系统真实 IO 与 Zip Slip 防护用例）
dotnet test MdExplorer.slnx -c Release

# 无头渲染五档断点截图到 tests/MdExplorer.Screenshots/bin/Release/net10.0/screenshots/
dotnet test tests/MdExplorer.Screenshots -c Release

# 桌面自包含发布 / Native AOT 发布
dotnet publish src/MdExplorer.Desktop -c Release -r linux-x64 --self-contained true
dotnet publish src/MdExplorer.Desktop -c Release -r linux-x64 -p:PublishAot=true

# Android APK（独立于 .slnx 构建，避免无 Android 工作负载的环境被卡）
dotnet publish src/MdExplorer.Android -c Release -p:PublishTrimmed=true -p:RunAOTCompilation=true
```

CI（`.github/workflows/ci.yml`）在每次推送时构建、出 UI 截图工件，并产出 `linux-x64` / `win-x64` 桌面包与 Android APK（Actions 页的 Artifacts 可直接下载试用）。

## 结构速览

```text
src/MdExplorer.Domain                领域类型/排序/格式化（零依赖）
src/MdExplorer.Platform.Abstractions 平台端口接口
src/MdExplorer.Application           名称校验/路径分段等应用规则
src/MdExplorer.Infrastructure        文件系统/操作队列/回收站/搜索/ZIP/设置
src/MdExplorer.UI                    共享 Avalonia 界面（五断点自适应壳）
src/MdExplorer.Desktop               桌面宿主      src/MdExplorer.Android  Android 宿主
tests/                               单元测试 + 无头截图冒烟
```

## 文档

| 文档 | 内容 |
|---|---|
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | 开发文档：选型与许可证/AOT 考据、分层架构、接口规范、功能规格、平台适配、AOT 合规、路线图与决策记录 |
| [docs/BRAND.md](docs/BRAND.md) | Eidoria（万象屿）命名、图标构造、核心色与品牌资产落点 |
| [docs/RESPONSIVE-LAYOUT.md](docs/RESPONSIVE-LAYOUT.md) | 五档断点布局规格（含 `docs/assets/layouts/` 五张 SVG 线框与高保真方向图） |
| [docs/CONCEPT-DESIGN.md](docs/CONCEPT-DESIGN.md) | 概念设计 v3：深浅主题成对稿、下一阶段规划界面、优先级与实现验收单 |
| [docs/IMAGE-PROMPTS.md](docs/IMAGE-PROMPTS.md) | GPT-image-2 效果图提示词（中英双语） |
| [docs/PLUGIN-SYSTEM.md](docs/PLUGIN-SYSTEM.md) | 插件系统计划与可行性评估（AOT 约束下的五机制选型、P0–P3 分期） |
| [docs/LUA-PLUGIN-SPIKE.md](docs/LUA-PLUGIN-SPIKE.md) | 隔离的 MoonSharp AOT/Trim/Android spike、可运行的安全演示与正式接入门槛（尚未验证） |
| [docs/LOCAL-AI.md](docs/LOCAL-AI.md) | 本地 AI 接入评估（多模态语义搜图、识图、TTS/ASR：模型许可、体积、A1–A4 分期） |

响应式断点（与 Material 3 一致，dp）：Compact `<600` / Medium `600–839` / Expanded `840–1199` / Large `1200–1599` / ExtraLarge `≥1600`。
