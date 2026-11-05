using System.Runtime.CompilerServices;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>基于 System.IO 的本地文件系统实现（桌面全量可用；Android 对真实路径位置可用）。</summary>
public sealed class LocalFileSystemService : IFileSystemService
{
    public async IAsyncEnumerable<FileItem> ListAsync(
        string folderPath, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 枚举在后台线程进行，避免大目录阻塞 UI；逐项产出，单项异常（竞态删除等）跳过不中断整体。
        var entries = await Task.Run(() =>
        {
            var result = new List<string>();
            using var enumerator = Directory.EnumerateFileSystemEntries(folderPath).GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? next;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    next = enumerator.Current;
                }
                catch (UnauthorizedAccessException) { break; }
                catch (IOException) { break; }
                if (next is not null) result.Add(next);
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);

        foreach (var path in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileItem? item;
            try
            {
                item = StatPath(path);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (item is null) continue;
            if (!includeHidden && item.IsHidden) continue;
            yield return item;
        }
    }

    public Task<FileItem?> StatAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try { return StatPath(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }, cancellationToken);

    public Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var full = Path.Combine(parentPath, name);
            Directory.CreateDirectory(full);
            return full;
        }, cancellationToken);

    public Task<string> CreateFileAsync(string parentPath, string name, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var full = Path.Combine(parentPath, name);
            using (File.Open(full, FileMode.CreateNew, FileAccess.Write)) { }
            return full;
        }, cancellationToken);

    public Task<string> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var parent = Path.GetDirectoryName(sourcePath) ?? throw new IOException("无法确定父目录");
            var dest = Path.Combine(parent, newName);
            if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, dest);
            else
                File.Move(sourcePath, dest);
            return dest;
        }, cancellationToken);

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 预览/缩略图/校验和都是只读旁路：Windows 下必须同时共享 Delete，
        // 否则用户刚选中文件（预览尚在读）就移动/删除时会被自己的预览句柄挡住。
        return Task.FromResult<Stream>(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, useAsync: true));
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public string? GetParentPath(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } parent
            ? parent
            : null;

    public IReadOnlyList<PathSegment> GetBreadcrumbs(string path) =>
        PathText.Segments(path).Select(s => new PathSegment(s.Name, s.FullPath)).ToArray();

    internal static FileItem? StatPath(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return null;
            return new FileItem(
                FileLocator.FromPath(dir.FullName),
                dir.Name.Length == 0 ? dir.FullName : dir.Name,
                FileKind.Folder,
                null,
                new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero),
                IsHidden(dir),
                (dir.Attributes & FileAttributes.ReparsePoint) != 0,
                null);
        }

        return new FileItem(
            FileLocator.FromPath(info.FullName),
            info.Name,
            FileKindClassifier.Classify(info.Name),
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            IsHidden(info),
            (info.Attributes & FileAttributes.ReparsePoint) != 0,
            info.Extension.Length == 0 ? null : info.Extension);
    }

    private static bool IsHidden(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.Hidden) != 0) return true;
        return info.Name.StartsWith('.') && info.Name.Length > 1;
    }
}
