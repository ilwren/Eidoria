# libmpv 桌面后端：接入说明、许可证门与待验收项

状态：**v0.39 起 libmpv 已从隔离 Spike 变成正式的可选后端**，与平台自带的系统解码栈并存，由用户在设置里选择。
`spikes/LibMpvAvaloniaSpike` 仍保留为最小可运行样例（只用来单独验证"包装层 + 原生库"能不能出画面），
正式应用走 `src/MdExplorer.Desktop/MpvMediaPlaybackService.cs`。

## 1. 为什么同时保留两条后端

| | 系统解码栈 | libmpv（可选） |
|---|---|---|
| 安装成本 | 零（Windows Media Foundation / Linux 发行版 GStreamer / Android MediaPlayer） | 用户自行放入 LGPL-only libmpv 原生库 |
| 视频画面 | Windows 用 `MediaComposition` 反复取缩略帧（约 12 fps，卡顿可见）；Linux 用 `appsink` 推帧；Android 用 `TextureView` | 包装层自带渲染器（GL / 软件 / Windows 原生子窗口），连续呈现 |
| 能播什么 | 由系统/发行版决定，探测结果如实给原因 | 由该 libmpv 构建带的 FFmpeg 组件决定 |
| 许可与体积 | 无新增依赖 | 应用只带 MIT 托管包装；原生库**不进包、不下载** |

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

### 渲染路线覆盖（排查用）

| 环境变量 | 值 | 说明 |
|---|---|---|
| `MDEXPLORER_LIBMPV_RENDERER` | `opengl`（默认） | Avalonia 内的 GL 控件，能跟随圆角裁剪、浮窗与缩放 |
| | `software` | CPU 渲染，最保守；分辨率高时开销大 |
| | `native` | Windows 原生子窗口（零拷贝、最快），但画面是独立图层，浮层盖不住它 |

## 3. 代码落点与行为

- 端口：`IMediaVideoControlSource` + `MediaRendererKind.HostedControl`（`src/MdExplorer.Platform.Abstractions/MediaPorts.cs`）。
  包装层的每个渲染视图都在自己的字段里创建 mpv 上下文，所以"控件存在"与"有解码器"是同一件事；
  宿主再插一脚只会得到两份解码器，因此这条路线是**后端交出控件**，而不是推帧或原生句柄。
- 探测：`MpvNativeLibrary` 用 `NativeLibrary.TryLoad` 试装一次（搜索顺序：`MDEXPLORER_LIBMPV_ROOT` → 应用目录 → 系统库路径）。
  库不在时**连控件都不创建**——包装层在构造视图时就会调 `mpv_create`，那是一个在布局过程中抛出的
  `DllNotFoundException`，用户只会看到闪退。
- 裁决：`MediaBackendSelector`（`src/MdExplorer.UI/Services/MediaBackendSelector.cs`）按偏好 + 能力位裁决；
  **能力位为假的后端不算可用**；显式点名却用不上时在诊断里写"已回退 + 原因"，Auto 不写"回退"。
- 状态回报：500 ms 属性轮询（`TimePos` / `Duration` / `Pause` / `EofReached` / `IdleActive`），
  音画由 mpv 自己的事件循环驱动，宿主不参与解码。
- 打开看门狗：5 s 内没有时长就如实报"这个构建打不开该文件"，不留一个永远"正在打开…"的卡片。
- 续播：宿主把画面控件摘掉（预览栏收起、切走目的地）时记住文件与位置，挂回来自动接着播。
- 音频：`includeVideoSurface=false` 时用软件渲染器，不为一个没有画面的文件去开 GL 表面；
  控件仍然要存在——没有控件就没有上下文，也就没有音频。

## 4. 许可证和分发门

mpv 默认可构建为 GPL；也支持 `-Dgpl=false` 的 LGPL 构建。libmpv 不是独立 codec：它依赖 FFmpeg，
因此本项目只接受 **LGPL-only FFmpeg** 作为其原生传递依赖，并须确认实际构建没有启用 GPL/nonfree 组件、
审计随构建带来的其余原生库、许可证文本与对应源码。

`HanumanInstitute.LibMpv.Avalonia`（MIT）**不提供可分发的原生库**：它只把 libmpv 原生二进制列为单独获取的内容。
本仓因此只引用托管包装（`src/MdExplorer.Desktop` 一个项目），不打包、不下载任何 libmpv 二进制，运行期也不联网。
若要长期分发"开箱即用"的桌面包，必须另做可复现的 LGPL-only 构建流水线（含源码与许可证清单），
并重新评估包体积与 LGPL 动态链接要求——这一条仍未做。

## 5. 仍待验收（本版不吹的地方）

1. **真机流畅度对比**：Windows（12 fps 取帧 vs libmpv 连续渲染）、Linux（`appsink` 推帧 vs GL）、硬件解码与 seek 抖动，
   都还没有实测数字。CI 无显示/音频设备，只能证明接线、契约与降级路径正确。
2. **Native AOT / Trim**：托管包装未声明 `IsAotCompatible`，通过函数指针调用原生接口。
   当前 AOT 发布 Job 仍是探路性质；若 AOT 下包装层不可用，探测通过但控件创建失败会**如实降级**（catch → 诊断文案），
   不会把进程带走。AOT 若成为发布门，需要为这条路径单独立项（预生成委托或排除该后端）。
3. **Android**：不提供 libmpv（另需可追溯的 LGPL-only Android 预编译库、ABI 覆盖、Surface 生命周期与 AOT 验证），
   继续用系统 `MediaPlayer` + `TextureView`。
4. **音频设备切换、字幕与音轨选择**：包装层能力够，但 UI 未开放；不做成"有按钮没效果"。
5. **崩溃隔离**：原生解码不可信文件时崩溃会带走进程（与 PDFium 同类风险）。当前只靠"打开失败如实报错"，
   没有做进程外解码；若要彻底隔离，需要进程外 worker（与插件 P2 同一条路）。

## 6. Spike 样例（`spikes/LibMpvAvaloniaSpike`）

隔离样例保留，用来单独验证"包装层 + 某份原生构建"能否出画面，不参与正式应用构建产物：

```powershell
$env:MDEXPLORER_LIBMPV_ROOT = 'C:\media\libmpv-lgpl\bin'
dotnet run --project spikes/LibMpvAvaloniaSpike -c Release
```

```bash
export MDEXPLORER_LIBMPV_ROOT="$HOME/opt/libmpv-lgpl/lib"
dotnet run --project spikes/LibMpvAvaloniaSpike -c Release
```

样例项目列在主 `.slnx` 中，CI 至少编译托管集成；它不携带原生二进制，编译通过不等于播放验收通过。
