using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 回收站实现（v0.1 桌面合一版）：
/// Linux 按 freedesktop Trash 规范完整实现（送站/枚举/还原/真删）；
/// Windows 以 SHFileOperationW(FOF_ALLOWUNDO) 送站（枚举留待后续，见开发文档 R-04）；
/// Android 等平台无系统回收站，能力位全关，UI 降级为永久删除确认流。
/// </summary>
public sealed class TrashService : ITrashService
{
    public bool CanMoveToTrash => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();
    public bool CanBrowseTrash => OperatingSystem.IsLinux();

    public async Task MoveToTrashAsync(string path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
            await LinuxTrash.MoveToTrashAsync(path, cancellationToken).ConfigureAwait(false);
        else if (OperatingSystem.IsWindows())
            await Task.Run(() => WindowsTrash.MoveToTrash(path), cancellationToken).ConfigureAwait(false);
        else
            throw new PlatformNotSupportedException("当前平台没有系统回收站");
    }

    public async Task<IReadOnlyList<TrashedItem>> ListAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
            return await LinuxTrash.ListAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task<TrashStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var items = await ListAsync(cancellationToken).ConfigureAwait(false);
        return new TrashStats(items.Sum(item => item.SizeBytes ?? 0), items.Count);
    }

    public async Task RestoreAsync(string trashItemId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
            await LinuxTrash.RestoreAsync(trashItemId, cancellationToken).ConfigureAwait(false);
        else
            throw new PlatformNotSupportedException();
    }

    public async Task DeletePermanentlyAsync(string trashItemId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
            await LinuxTrash.DeletePermanentlyAsync(trashItemId, cancellationToken).ConfigureAwait(false);
        else
            throw new PlatformNotSupportedException();
    }

    public async Task EmptyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("当前平台无法在应用内清空系统回收站");
        foreach (var item in await LinuxTrash.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await LinuxTrash.DeletePermanentlyAsync(item.Id, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>freedesktop Trash 规范（Home Trash：$XDG_DATA_HOME/Trash 或 ~/.local/share/Trash)。</summary>
internal static class LinuxTrash
{
    private static string TrashHome
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var baseDir = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
            return Path.Combine(baseDir, "Trash");
        }
    }

    private static string FilesDir => Path.Combine(TrashHome, "files");
    private static string InfoDir => Path.Combine(TrashHome, "info");

    public static Task MoveToTrashAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(InfoDir);
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full.TrimEnd('/'));
        var trashName = name;
        var i = 2;
        while (File.Exists(Path.Combine(InfoDir, trashName + ".trashinfo")) ||
               File.Exists(Path.Combine(FilesDir, trashName)) ||
               Directory.Exists(Path.Combine(FilesDir, trashName)))
        {
            var ext = Path.GetExtension(name);
            trashName = $"{Path.GetFileNameWithoutExtension(name)}.{i++}{ext}";
        }

        var infoText = "[Trash Info]\n" +
                       $"Path={Uri.EscapeDataString(full).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}\n" +
                       $"DeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
        File.WriteAllText(Path.Combine(InfoDir, trashName + ".trashinfo"), infoText);
        try
        {
            var dest = Path.Combine(FilesDir, trashName);
            if (Directory.Exists(full))
                Directory.Move(full, dest);
            else
                File.Move(full, dest);
        }
        catch
        {
            try { File.Delete(Path.Combine(InfoDir, trashName + ".trashinfo")); } catch (IOException) { }
            throw;
        }
    }, ct);

    public static Task<IReadOnlyList<TrashedItem>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<TrashedItem>>(() =>
    {
        var items = new List<TrashedItem>();
        if (!Directory.Exists(InfoDir)) return items;
        foreach (var infoFile in Directory.EnumerateFiles(InfoDir, "*.trashinfo"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? original = null;
                DateTimeOffset deleted = default;
                foreach (var line in File.ReadLines(infoFile))
                {
                    if (line.StartsWith("Path=", StringComparison.Ordinal))
                        original = Uri.UnescapeDataString(line["Path=".Length..]);
                    else if (line.StartsWith("DeletionDate=", StringComparison.Ordinal) &&
                             DateTimeOffset.TryParse(line["DeletionDate=".Length..], out var parsed))
                        deleted = parsed;
                }

                if (original is null) continue;
                var trashName = Path.GetFileNameWithoutExtension(infoFile);
                var payload = Path.Combine(FilesDir, trashName);
                items.Add(new TrashedItem(
                    trashName,
                    Path.GetFileName(original),
                    original,
                    deleted,
                    Directory.Exists(payload),
                    ComputePayloadSize(payload)));
            }
            catch (IOException) { /* 单条 info 损坏不中断枚举 */ }
            catch (UnauthorizedAccessException) { }
        }
        items.Sort((a, b) => b.DeletedAt.CompareTo(a.DeletedAt));
        return items;
    }, ct);

    public static Task RestoreAsync(string trashItemId, CancellationToken ct) => Task.Run(() =>
    {
        var item = Find(trashItemId) ?? throw new IOException("回收站条目不存在");
        var payload = Path.Combine(FilesDir, trashItemId);
        var parent = Path.GetDirectoryName(item.OriginalPath);
        if (parent is not null) Directory.CreateDirectory(parent);
        if (Directory.Exists(payload))
            Directory.Move(payload, item.OriginalPath);
        else
            File.Move(payload, item.OriginalPath);
        File.Delete(Path.Combine(InfoDir, trashItemId + ".trashinfo"));
    }, ct);

    public static Task DeletePermanentlyAsync(string trashItemId, CancellationToken ct) => Task.Run(() =>
    {
        var payload = Path.Combine(FilesDir, trashItemId);
        FileOperationService.DeletePath(payload, ct);
        var info = Path.Combine(InfoDir, trashItemId + ".trashinfo");
        if (File.Exists(info)) File.Delete(info);
    }, ct);

    private static long? ComputePayloadSize(string payload)
    {
        try
        {
            if (File.Exists(payload))
                return new FileInfo(payload).Length;
            if (!Directory.Exists(payload))
                return null;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                try { total = checked(total + new FileInfo(file).Length); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (OverflowException) { return long.MaxValue; }
            }
            return total;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static TrashedItem? Find(string trashItemId)
    {
        var infoFile = Path.Combine(InfoDir, trashItemId + ".trashinfo");
        if (!File.Exists(infoFile)) return null;
        string? original = null;
        foreach (var line in File.ReadLines(infoFile))
            if (line.StartsWith("Path=", StringComparison.Ordinal))
                original = Uri.UnescapeDataString(line["Path=".Length..]);
        return original is null ? null : new TrashedItem(trashItemId, Path.GetFileName(original), original, default, false);
    }
}

[SupportedOSPlatform("windows")]
internal static partial class WindowsTrash
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted; // BOOL 以 int 承载，保证结构体可 blittable 供 LibraryImport 生成
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperationW(ref SHFILEOPSTRUCTW fileOp);

    public static void MoveToTrash(string path)
    {
        // pFrom 须为双空终止的路径串
        var from = Marshal.StringToHGlobalUni(Path.GetFullPath(path) + "\0\0");
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var hr = SHFileOperationW(ref op);
            if (hr != 0 || op.fAnyOperationsAborted != 0)
                throw new IOException($"移入回收站失败（HRESULT 0x{hr:X8}）");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
