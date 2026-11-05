# libmpv Avalonia 桌面 Spike

这是一个独立验证样例，仍可单独检查 `HanumanInstitute.LibMpv.Avalonia` 0.10.1（MIT managed wrapper，目标为 .NET 10 / Avalonia 12）的控件与原生库加载。Eidoria 桌面程序现在也有一个可选择的 libmpv 预览后端；两处都使用单独提供的 libmpv 原生库，仓库不携带或打包任何 mpv 二进制。正式应用的选择、接线与对比步骤见 [`MEDIA-PLAYBACK.md`](MEDIA-PLAYBACK.md)。Spike 继续列在主 `.slnx` 中，以便 CI 至少编译 managed 集成；编译通过不等于已通过原生播放验收。

## 运行

先取得一个 **LGPL-only** 的 libmpv 0.40+ 原生构建及其全部动态依赖。启动应用时 `MDEXPLORER_LIBMPV_ROOT` 应指向包含 libmpv 主库和依赖库的目录；未设置时样例按 wrapper 默认规则在应用目录/系统库路径中查找。

PowerShell 示例（路径需替换为你审核过的 libmpv/FFmpeg LGPL-only 构建目录）：

```powershell
$env:MDEXPLORER_LIBMPV_ROOT = 'C:\media\libmpv-lgpl\bin'
dotnet run --project spikes/LibMpvAvaloniaSpike -c Release
```

Linux 示例：

```bash
export MDEXPLORER_LIBMPV_ROOT="$HOME/opt/libmpv-lgpl/lib"
dotnet run --project spikes/LibMpvAvaloniaSpike -c Release
```

不设置环境变量时，Linux 也可使用系统的 `libmpv`；但必须先核查发行版构建配置与传递依赖的许可证。样例只接受本地路径，视频窗使用 wrapper 的 Avalonia renderer，不把解码帧回传到 `MediaFrameSurface`。

### 可直接下载的试用构建

- **Windows x64（首选试 Spike）**：社区仓库 [w3bward/libmpv-lgpl](https://github.com/w3bward/libmpv-lgpl/releases/tag/mpv-0.41.0-r1) 发布了基于固定源码的 LGPL libmpv 0.41.0 包，包含 `libmpv-2.dll`、依赖 DLL、headers、许可证目录和构建信息。发布说明给出 ZIP SHA-256：`1522cc89007411a923f83a6402529a9c403c093bb8a4ade918b39aeae91805bc`。此构建的 FFmpeg 禁用了网络支持，适合当前本地文件 Spike；它是社区构建，验收前仍应自行核对校验和与包内容。
- **Windows x64/ARM64（上游开发构建）**：mpv 的 [development release](https://github.com/mpv-player/mpv/releases/tag/git-release) 提供标注为 LGPL 的 libmpv ZIP，使用 `-Dgpl=false` 和 LGPL FFmpeg 变体；它是未稳定的开发快照，不建议作为正式依赖。
- **Linux**：多个发行版有系统 `libmpv` 包，但各发行版的 mpv/FFmpeg 编译选项与依赖许可并不一致，不能仅凭包名认定为 LGPL-only。尚未找到适用于所有发行版、可直接放心捆绑的统一官方 Linux LGPL 运行包。

所以现在可以先用现成 Windows x64 包运行这个 Spike；若之后需要**可复现、可审计的 Linux 下载包**，可以另做手动触发的 GitHub Actions 工作流，从固定源码构建 `-Dgpl=false` 的 mpv 与 LGPL FFmpeg，再附上所有运行库、许可证、源码和校验和。仅把 distro 的 `libmpv.so` 复制进 artifact 不足以满足许可与可移植性要求。

## 许可证和分发门

mpv 项目默认可构建为 GPL；它也支持 `-Dgpl=false` 的 LGPL 构建。libmpv 不是独立 codec：它依赖 FFmpeg，因此这个 Spike 接受 **LGPL-only FFmpeg** 作为 libmpv 的原生传递依赖，但必须确认实际构建没有启用 GPL/nonfree 组件，并审计随构建带来的其余原生库、许可证文本和对应源码。不能只看 C# NuGet 包标注的 MIT 就认定整个运行时是 MIT。

`HanumanInstitute.LibMpv.Avalonia` NuGet 包不提供正式应用可直接分发的多平台原生库；其 README 明确把 libmpv 原生二进制列为单独获取的内容，且由社区维护。当前样例仅覆盖 Windows/Linux 桌面，不含 Android。它也尚未验证 Native AOT/Trim；managed wrapper 项目没有声明 `IsAotCompatible` / `IsTrimmable`，而 API 通过函数指针/委托解析调用原生接口，因此正式集成前必须做 AOT/Trim 发布与真机验证。

## 这次要回答的问题

1. Avalonia 12 的桌面渲染器能否稳定承载 libmpv OpenGL/native renderer，并与预览面板裁剪、缩放及窗口重建共存。
2. 审核后的 LGPL-only 原生构建在 Windows/Linux 的加载、codec 覆盖、硬件解码、seek、音频输出和重复打开/释放是否可靠。
3. 包体积、transitive DLL/SO、许可证清单以及 Native AOT/Trim 是否可接受。
4. Android 仍保持现有 `MediaPlayer`/`MediaCodec` 路线；本 Spike 不把桌面 wrapper 的平台兼容性推断成 Android 支持。要统一到 Android，另需确认可追溯的 Android LGPL-only libmpv + FFmpeg 预编译库、ABI 覆盖、许可源码、Surface 生命周期和 AOT/Trim。
