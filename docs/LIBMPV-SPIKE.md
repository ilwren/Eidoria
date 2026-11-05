# libmpv 桌面后端：接入说明、许可证门与待验收项

状态：**v0.39 起 libmpv 是正式的桌面端可选后端**，与平台自带的系统解码栈并存，由用户在设置里选择。
`spikes/LibMpvAvaloniaSpike` 保留为历史参考（已从解决方案里移除，见 §6）。

## 1. 为什么同时保留两条后端

| | 系统解码栈 | libmpv（可选） |
|---|---|---|
| 安装成本 | 零（Windows Media Foundation / Linux 发行版 GStreamer / Android MediaPlayer） | 用户自行放入 LGPL-only libmpv 原生库 |
| 视频画面 | Windows 用 `MediaComposition` 反复取缩略帧（约 12 fps，卡顿可见）；Linux 用 `appsink` 推帧；Android 用 `TextureView` | 真正的连续解码 + 音视频同步，软件渲染成 BGRA 后推帧 |
| 能播什么 | 由系统/发行版决定，探测结果如实给原因 | 由该 libmpv 构建带的 FFmpeg 组件决定 |
| 许可与体积 | 无新增依赖 | 应用只带 MIT 托管包；原生库**不进包、不下载** |

结论先摆在桌面上：**Windows 上系统栈的取帧路线确实不够流畅**，这正是加 libmpv 的原因；
但 libmpv 需要用户动手准备原生库，因此两条都留，让实际观感决定去留（D-03）。

## 2. 启用方式

1. 取得一份 **LGPL-only** 的 libmpv 0.40+ 原生构建（`-Dgpl=false` + LGPL FFmpeg 变体）。
   - Windows x64 可试用社区构建 [w3bward/libmpv-lgpl](https://github.com/w3bward/libmpv-lgpl/releases)（基于固定源码，含 `libmpv-2.dll`、依赖 DLL 与许可证目录；发布说明给出 ZIP SHA-256，验收前请自行核对）。
   - Windows x64/ARM64 也可用 mpv 的 [development release](https://github.com/mpv-player/mpv/releases/tag/git-release) 中标为 LGPL 的 libmpv ZIP（未稳定的开发快照，不建议作为正式依赖）。
   - Linux 多数发行版有 `libmpv` 包，但各发行版的 mpv/FFmpeg 编译选项并不一致，**不能仅凭包名认定为 LGPL-only**。
2. 把原生库及其依赖放到 **Eidoria 程序目录**（最稳：Windows 的依赖解析默认看 exe 所在目录），或者设置
   `MDEXPLORER_LIBMPV_ROOT` 指向那个目录。若放在别处，Windows 还需要该目录在 `PATH` 中，否则依赖解析不到。
3. 启动应用 → 设置 → 媒体 → 「内嵌播放后端」选 **自动**（优先 libmpv）或 **libmpv**。
   选完不重启也行，但**切换后端会停止当前播放**——跨后端保住播放状态是不可能的，界面不装作可以。

诊断入口：预览卡的「媒体后端诊断」会显示后端名、画面交付方式与裁决结论（用了谁、是否回退、为什么）。

## 3. 代码落点与行为

- 依赖：**只引用 `HanumanInstitute.LibMpv` 核心包**（MIT、无 UI 依赖）。不引用 `HanumanInstitute.LibMpv.Avalonia`，
  原因见 §4。
- 画面：`MpvMediaPlaybackService`（`src/MdExplorer.Desktop`）构造 `vo=libmpv` 的 `MpvContext`，
  调 `StartSoftwareRendering(updateCallback)`；更新回调只按一下信号量，**真正的 `SoftwareRender` 在独立的
  `libmpv-render` 线程上执行**（libmpv 要求渲染固定线程、且不要在更新回调里直接渲染），渲染结果转成 BGRA
  推给宿主的 `IMediaFrameSink`——这是 `MediaRendererKind.FramePush`，与 Linux GStreamer 同一条通道。
  渲染尺寸按视频宽高比、以 960×540 封顶（预览卡只有两三百像素高，没必要做 1080p 的 CPU 转换）。
- 探测：`MpvNativeLibrary` 用 `NativeLibrary.TryLoad` 试装一次（搜索顺序：`MDEXPLORER_LIBMPV_ROOT` → 应用目录 → 系统库路径）。
  库不在时**连上下文都不创建**——包装层在构造上下文时就会调 `mpv_create`，那是一个在布局过程中抛出的
  `DllNotFoundException`，用户只会看到闪退。
- 裁决：`MediaBackendSelector`（`src/MdExplorer.UI/Services`）按偏好 + 能力位裁决；**能力位为假的后端不算可用**；
  显式点名却用不上时在诊断里写「已回退 + 原因」，Auto 只写「谁没被用上、为什么」（Auto 不是回退）。
  裁决层通过 `IMediaBackendSelector.ActiveBackend` 交出真正在跑的后端——它自己装不出
  `IMediaVideoFrameSource`，否则预览会以为后端不出画面。
- 状态回报：500 ms 属性轮询（`TimePos` / `Duration` / `Pause` / `EofReached` / `IdleActive`），
  音画由 mpv 自己的线程驱动，宿主不参与解码。
- 打开看门狗：5 s 内没有时长就如实报「这个构建打不开该文件」，不留一个永远「正在打开…」的卡片。

## 4. 为什么不用官方的 `LibMpv.Avalonia` 包装

它的 `net10.0` 目标直接 `<PackageReference Include="ReactiveUI.Avalonia" Version="12.0.1" />`，于是：

1. **违反 §3.2 禁用清单**：`ReactiveUI.*` 在本项目是 PR 检查项级别的禁用依赖（反射式激活、AOT 不友好）。
2. **在 Linux 上直接红**：`ReactiveUI 23.2.1` / `Splat 19.3.1` 等包的作者签名证书被吊销，
   NuGet 在 Linux 上默认做签名校验，restore 直接失败 `NU3012`（Windows 不校验，所以只有 linux 发布 Job 红）。
   本轮 CI 先红在这里，日志已回传 `ci-artifacts` 分支（`media-probe/`）。

用核心包自己驱动渲染，代价是多写了约 100 行渲染循环，换来：依赖边界干净、不需要 GL 互操作、
画面通道复用已有的推帧实现（截图与单元测试都已覆盖 FramePush）。

## 5. 许可证和分发门

mpv 默认可构建为 GPL；也支持 `-Dgpl=false` 的 LGPL 构建。libmpv 不是独立 codec：它依赖 FFmpeg，
因此本项目只接受 **LGPL-only FFmpeg** 作为其原生传递依赖，并须确认实际构建没有启用 GPL/nonfree 组件、
审计随构建带来的其余原生库、许可证文本与对应源码。

`HanumanInstitute.LibMpv`（MIT）**不提供可分发的原生库**：它只把 libmpv 原生二进制列为单独获取的内容。
本仓因此只引用托管包（`src/MdExplorer.Desktop` 一个项目），不打包、不下载任何 libmpv 二进制，运行期也不联网。
若要长期分发「开箱即用」的桌面包，必须另做可复现的 LGPL-only 构建流水线（含源码与许可证清单），
并重新评估包体积与 LGPL 动态链接要求——这一条仍未做。

## 6. 仍待验收（本版不吹的地方）

1. **真机流畅度对比**：Windows（12 fps 取帧 vs libmpv 连续推帧）、Linux（`appsink` 推帧 vs libmpv 软件渲染）、
   硬件解码与 seek 抖动，都还没有实测数字。CI 无显示/音频设备，只能证明接线、契约与降级路径正确。
2. **软件渲染的 CPU 成本**：默认按 960×540 封顶 + 视频宽高比，仍未做实测；若明显吃 CPU，
   下一步是接 `StartOpenGlRendering`（Avalonia 的 `OpenGlControlBase` 已有可用的 GL 上下文接口）。
3. **Native AOT / Trim**：核心包未声明 `IsAotCompatible`，通过函数指针调用原生接口。
   v0.39 实测 linux-x64 Native AOT 发布**能过**，只留下 `IL2104`（`HanumanInstitute.LibMpv`、
   `HanumanInstitute.Validators` 产生 trim 警告）与 `IL3053`（Validators 的 AOT 分析警告）——
   都是包侧警告，本仓 `TreatWarningsAsErrors=false`，AOT 门转正时需要连同这两条一起评估。
   AOT 下若运行期不可用，探测通过但上下文创建失败会**如实降级**（catch → 诊断文案），不会把进程带走。
4. **Android**：不提供 libmpv（另需可追溯的 LGPL-only Android 预编译库、ABI 覆盖、Surface 生命周期与 AOT 验证），
   继续用系统 `MediaPlayer` + `TextureView`。
5. **音频设备切换、字幕与音轨选择**：包装层能力够，但 UI 未开放；不做成「有按钮没效果」。
6. **崩溃隔离**：原生解码不可信文件时崩溃会带走进程（与 PDFium 同类风险）。当前只靠「打开失败如实报错」，
   没有做进程外解码；若要彻底隔离，需要进程外 worker（与插件 P2 同一条路）。

## 7. Spike 样例（`spikes/LibMpvAvaloniaSpike`）

隔离样例保留在仓库里作为「官方 Avalonia 包装长什么样」的历史参考，**已从 `MdExplorer.slnx` 移除**：
它引用的 `HanumanInstitute.LibMpv.Avalonia` 会把 `ReactiveUI.*` 拉进依赖图（见 §4），
而解决方案级构建会把这份禁用依赖带进来。要单独研究它，请在自己的分支里恢复引用并只在 Windows 上试跑。

现在的应用侧验证路径就是设置页切后端 + 预览卡诊断，不再需要单独的 spike。
