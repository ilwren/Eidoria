using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>基于 DriveInfo 的卷发现（过滤未就绪与明显伪文件系统）。</summary>
public sealed class VolumeService : IVolumeService
{
    public Task<IReadOnlyList<StorageVolume>> GetVolumesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<StorageVolume>>(() =>
        {
            var volumes = new List<StorageVolume>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady) continue;
                    var root = drive.RootDirectory.FullName;
                    // Linux 上过滤虚拟挂载（DriveInfo 在 Linux 会列出 /proc 等）
                    if (OperatingSystem.IsLinux() && IsPseudoMount(root, drive.DriveFormat))
                        continue;
                    var display = DisplayName(drive, root);
                    volumes.Add(new StorageVolume(
                        root, display, FileLocator.FromPath(root),
                        SafeTotal(drive), SafeFree(drive),
                        drive.DriveType == DriveType.Removable));
                }
                catch (IOException) { /* 单卷异常不中断 */ }
                catch (UnauthorizedAccessException) { }
            }

            return volumes;
        }, cancellationToken);

    private static long? SafeTotal(DriveInfo d) { try { return d.TotalSize > 0 ? d.TotalSize : null; } catch (IOException) { return null; } }
    private static long? SafeFree(DriveInfo d) { try { return d.TotalFreeSpace; } catch (IOException) { return null; } }

    private static string DisplayName(DriveInfo drive, string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var label = SafeLabel(drive);
            var stem = root.TrimEnd('\\');
            return string.IsNullOrWhiteSpace(label) ? $"本地磁盘 ({stem})" : $"{label} ({stem})";
        }

        if (root == "/") return "文件系统";
        var name = Path.GetFileName(root.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? root : name;
    }

    private static string? SafeLabel(DriveInfo d) { try { return d.VolumeLabel; } catch (IOException) { return null; } }

    private static bool IsPseudoMount(string root, string? format)
    {
        if (root.StartsWith("/proc", StringComparison.Ordinal) ||
            root.StartsWith("/sys", StringComparison.Ordinal) ||
            root.StartsWith("/dev", StringComparison.Ordinal) ||
            root.StartsWith("/run", StringComparison.Ordinal) ||
            root.StartsWith("/snap", StringComparison.Ordinal))
            return true;
        return format is "proc" or "sysfs" or "devtmpfs" or "tmpfs" or "ramfs" or "cgroup" or "cgroup2"
            or "overlay" or "squashfs" or "devpts" or "mqueue" or "securityfs" or "debugfs" or "fusectl" or "configfs";
    }
}

/// <summary>已知目录：以 Environment.SpecialFolder 为基，Linux 叠加 XDG user-dirs.dirs 重定向。</summary>
public sealed class KnownFoldersService : IKnownFoldersService
{
    private readonly Lazy<IReadOnlyDictionary<KnownFolder, string>> _linuxUserDirs = new(ParseLinuxUserDirs);

    public string? Get(KnownFolder folder)
    {
        if (OperatingSystem.IsLinux() && _linuxUserDirs.Value.TryGetValue(folder, out var linuxPath))
            return linuxPath;

        var special = folder switch
        {
            KnownFolder.Home => Environment.SpecialFolder.UserProfile,
            KnownFolder.Documents => Environment.SpecialFolder.MyDocuments,
            KnownFolder.Pictures => Environment.SpecialFolder.MyPictures,
            KnownFolder.Music => Environment.SpecialFolder.MyMusic,
            KnownFolder.Videos => Environment.SpecialFolder.MyVideos,
            KnownFolder.Desktop => Environment.SpecialFolder.DesktopDirectory,
            KnownFolder.Downloads => (Environment.SpecialFolder?)null, // 无直接枚举，下方推导
            _ => null,
        };

        string? path;
        if (special is { } s)
        {
            path = Environment.GetFolderPath(s);
        }
        else if (folder == KnownFolder.Downloads)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads");
        }
        else
        {
            path = null;
        }

        return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
    }

    private static IReadOnlyDictionary<KnownFolder, string> ParseLinuxUserDirs()
    {
        var result = new Dictionary<KnownFolder, string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
                configHome = Path.Combine(home, ".config");
            var file = Path.Combine(configHome, "user-dirs.dirs");
            if (!File.Exists(file)) return result;

            var keyMap = new Dictionary<string, KnownFolder>(StringComparer.Ordinal)
            {
                ["XDG_DESKTOP_DIR"] = KnownFolder.Desktop,
                ["XDG_DOWNLOAD_DIR"] = KnownFolder.Downloads,
                ["XDG_DOCUMENTS_DIR"] = KnownFolder.Documents,
                ["XDG_PICTURES_DIR"] = KnownFolder.Pictures,
                ["XDG_MUSIC_DIR"] = KnownFolder.Music,
                ["XDG_VIDEOS_DIR"] = KnownFolder.Videos,
            };

            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq];
                if (!keyMap.TryGetValue(key, out var folder)) continue;
                var value = line[(eq + 1)..].Trim().Trim('"')
                    .Replace("$HOME", home, StringComparison.Ordinal);
                if (value.Length > 0 && Directory.Exists(value))
                    result[folder] = value;
            }
        }
        catch (IOException) { /* user-dirs 不可读时回落 SpecialFolder */ }
        catch (UnauthorizedAccessException) { }

        return result;
    }
}
