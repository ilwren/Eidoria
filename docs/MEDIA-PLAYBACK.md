# 媒体预览后端对比

## 当前路线

Windows 桌面设置里的 **媒体 → Windows 内嵌播放后端** 可选择：

- **系统 API**：保留现有 Media Foundation / WinRT `MediaPlayer` 音频路线；视频由 `MediaComposition` 按播放位置取帧，再通过 Avalonia 帧呈现面绘制。此方案不额外携带原生解码库，但高分辨率视频的取帧成本可能造成卡顿或音画不同步。
- **libmpv**：使用 `HanumanInstitute.LibMpv.Avalonia` 的 Avalonia renderer 直接输出画面，不经过应用的逐帧截图/拷贝路径。该方案仍需单独提供 libmpv 原生库及其全部依赖；应用不会打包这些文件，也不会在 libmpv 不可用时静默改用系统后端。

设置会持久化，**重启应用后切换**。`MediaBackend` 的旧设置缺省为 `System`；未知值也规范化为 `System`。Android 继续使用现有系统 MediaPlayer 路线，Linux 默认继续使用发行版 GStreamer，当前 UI 仅在 Windows 显示两后端选择。

视频默认仍不自动显示画面。要进行内嵌对比，可在设置中开启“视频默认在应用内嵌播放”，或在单个视频预览卡里选择“仍要内嵌观看”。预览会显示实际活动后端的名称、renderer 和诊断，不应把“选择了 libmpv”误认为“libmpv 已成功加载”；即使关闭常规诊断，后端未就绪或 renderer 初始化失败的原因仍会显示。

## renderer 宿主生命周期

侧栏与 Compact/Medium 浮动预览共享同一个 `PreviewViewModel` 和播放服务，但 renderer 控件不会共享或被两个视图同时持有。`MainView` 先停用旧预览，再激活新预览；只有当前活动且已进入视觉树的 `PreviewView` 才会创建直接 Avalonia renderer，离开时立即卸载。Android 原生 surface 与 Windows/Linux 推帧 surface 也受相同门控，切换或 detach 时分别释放原生 surface / 帧接收器。libmpv 在 context detach 时保存播放位置和播放意图、暂停旧 context，并用 generation 作废尚未完成的旧加载；新 context 到达后从保存位置加载，再按原播放意图恢复。该生命周期仍需通过 Windows 实机播放与侧栏/浮窗切换验证。

## 准备 Windows libmpv

1. 准备与 Eidoria 进程架构相同的 **LGPL-only** libmpv 构建，以及其所有运行时 DLL。核对 mpv 与 FFmpeg 是否禁用了 GPL/nonfree 组件、构建来源、校验和和许可证/源码材料。MIT managed wrapper 的许可证不覆盖 libmpv、FFmpeg 或其传递依赖。
2. 将 `MDEXPLORER_LIBMPV_ROOT` 指向包含 `libmpv-2.dll` 与依赖 DLL 的目录。示例：

   ```powershell
   $env:MDEXPLORER_LIBMPV_ROOT = 'C:\media\libmpv-lgpl\bin'
   dotnet run --project src/MdExplorer.Desktop -c Release
   ```

3. 在 Eidoria 设置中选择 **libmpv**，重启应用，再选择同一份本地测试视频。若原生库或依赖未通过加载预检，预览会如实显示不可用原因，不会替换成系统解码器。
4. 测完后选择 **系统 API** 并重启即可回到原路线。原 Windows 系统后端没有删除或改成 mpv。

未设置 `MDEXPLORER_LIBMPV_ROOT` 时，后端也会尝试从应用目录和系统原生库搜索路径加载；若这样使用，仍需自行核查实际库的来源和许可。显式设置目录时只接受该目录中的主库与依赖，不偷偷使用另一个安装位置的 libmpv。

## 对比建议

使用同一台 Windows 设备、同一文件、相同窗口尺寸与系统电源模式，分别记录启动时间、实际帧率/卡顿、CPU/GPU 占用、音画同步、seek 响应、暂停/恢复与关闭后的资源释放。最好至少覆盖一种 1080p H.264/AAC 文件和用户实际会看的高分辨率/高码率文件。当前没有把性能差异预先写成结论，也没有自动回退；哪条路线保留应以 Windows 实测结果决定。

无原生 mpv 包时，项目能编译并不代表 libmpv 播放已验收。该后端目前不面向 Android、不改变 AOT 范围，Native AOT/Trim 兼容与正式分发审核仍在本次媒体对比范围之外。
