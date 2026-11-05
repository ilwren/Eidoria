using System.Runtime.InteropServices;
using HanumanInstitute.LibMpv.Core;

namespace MdExplorer.Desktop;

/// <summary>libmpv 原生库的落地情况。只报事实：装没装、从哪加载、为什么不行。</summary>
internal sealed record MpvNativeLibraryStatus(bool IsAvailable, string Detail, string? LibraryPath)
{
    public static MpvNativeLibraryStatus Missing(string detail) => new(false, detail, null);
}

/// <summary>
/// 找一次 libmpv 原生库，结论缓存到进程结束。
/// <para>
/// 为什么要自己先探一次：托管包装在构造渲染控件时就会调 <c>mpv_create</c>，
/// 库不在会直接抛 <see cref="DllNotFoundException"/> —— 那是一个在布局过程中炸出来的异常，
/// 用户只会看到闪退。先用 <see cref="NativeLibrary.TryLoad(string, out nint)"/> 试装一次，
/// 失败就如实降级到平台栈，连控件都不创建。
/// </para>
/// <para>
/// 搜索顺序与包装层一致：<c>MDEXPLORER_LIBMPV_ROOT</c>（若设置，同时写进 <see cref="MpvApi.RootPath"/>）
/// → 应用目录（AOT/单文件发布时依赖 DLL 放在 exe 旁最稳）→ 系统库路径（Linux 发行版包）。
/// Windows 上把 libmpv 与它的 FFmpeg 依赖放在同一个目录里即可；放在别处时该目录还需要在 <c>PATH</c> 中，
/// 否则依赖解析不到（这是 Win32 的规则，不是本应用的策略）。
/// </para>
/// </summary>
internal static class MpvNativeLibrary
{
    /// <summary>原生库目录覆盖（与 docs/LIBMPV-SPIKE.md 里的样例共用同一个环境变量）。</summary>
    public const string RootVariable = "MDEXPLORER_LIBMPV_ROOT";

    private static readonly Lazy<MpvNativeLibraryStatus> Probe = new(ProbeCore);

    public static MpvNativeLibraryStatus Status => Probe.Value;

    public static void ConfigureRootPath()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
            return;
        try
        {
            if (Directory.Exists(root))
                MpvApi.RootPath = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 路径写错不该把应用带走：包装层会退回它自己的默认搜索路径。
        }
    }

    private static MpvNativeLibraryStatus ProbeCore()
    {
        ConfigureRootPath();

        var names = CandidateNames();
        var directories = new List<string>();
        void AddDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;
            try
            {
                var full = Path.GetFullPath(directory);
                if (!directories.Contains(full, StringComparer.Ordinal))
                    directories.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        AddDirectory(MpvApi.RootPath);
        AddDirectory(AppContext.BaseDirectory);

        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                string full;
                try
                {
                    full = Path.Combine(directory, name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!File.Exists(full))
                    continue;
                if (TryLoad(full))
                    return new MpvNativeLibraryStatus(true, $"已找到 libmpv：{full}", full);
            }
        }

        // 系统库路径：Linux 发行版包（libmpv.so.2）通常在这里被动态链接器接手。
        foreach (var name in names)
        {
            if (TryLoad(name))
                return new MpvNativeLibraryStatus(true, $"已通过系统库路径找到 libmpv（{name}）。", null);
        }

        var hint = OperatingSystem.IsWindows()
            ? "把经审核的 LGPL-only libmpv 构建（libmpv-2.dll 及其依赖 DLL）放到 Eidoria 程序目录，或设置 "
            : "安装发行版的 libmpv 包，或设置 ";
        return MpvNativeLibraryStatus.Missing(
            $"未找到可加载的 libmpv 原生库（{string.Join(" / ", names)}）。{hint}{RootVariable} 指向包含它的目录。");
    }

    private static IReadOnlyList<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
            return ["libmpv-2.dll", "mpv-2.dll"];
        if (OperatingSystem.IsMacOS())
            return ["libmpv.2.dylib", "libmpv.dylib"];
        return ["libmpv.so.2", "libmpv.so.1", "libmpv.so"];
    }

    private static bool TryLoad(string pathOrName)
    {
        try
        {
            if (!NativeLibrary.TryLoad(pathOrName, out var handle))
                return false;
            // 只验证“能不能加载（含依赖解析）”；真正的加载交给包装层，这里立刻放手。
            try
            {
                NativeLibrary.Free(handle);
            }
            catch (Exception)
            {
                // Free 失败不影响结论：我们已经知道这个路径可加载。
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
