using System.IO.Compression;
using System.Runtime.CompilerServices;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>递归文件名搜索：深度优先产出、不穿越目录符号链接、无权限子树跳过。</summary>
public sealed class SearchService : ISearchService
{
    public async IAsyncEnumerable<FileItem> SearchByNameAsync(
        SearchSpec spec, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = spec.Query.Trim();
        var hasKinds = spec.Kinds is { Count: > 0 };
        // 空查询仅在指定类型过滤时有意义（如主页「图片」分类）；否则视为无搜索
        if ((query.Length == 0 && !hasKinds) || !Directory.Exists(spec.RootPath))
            yield break;

        var found = 0;
        var pending = new Stack<string>();
        pending.Push(spec.RootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            List<string> entries;
            try
            {
                entries = await Task.Run(() => Directory.EnumerateFileSystemEntries(current).ToList(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileItem? item;
                try { item = LocalFileSystemService.StatPath(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (item is null) continue;

                var isHiddenDir = item.IsHidden && item.IsFolder;
                if (item.IsFolder && !item.IsSymlink && (spec.IncludeHidden || !isHiddenDir))
                    pending.Push(entry);

                if (!spec.IncludeHidden && item.IsHidden) continue;
                if (hasKinds && !spec.Kinds!.Contains(item.Kind)) continue;
                if (query.Length > 0 && !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                yield return item;
                if (++found >= spec.MaxResults) yield break;
            }
        }
    }
}

/// <summary>ZIP 压缩包（System.IO.Compression 实现），含 Zip Slip 防护。</summary>
public sealed class ArchiveService : IArchiveService
{
    public bool CanOpen(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ArchiveEntryItem>> ListEntriesAsync(
        string archivePath, int maxEntries, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ArchiveEntryItem>>(() =>
        {
            var items = new List<ArchiveEntryItem>();
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isFolder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                items.Add(new ArchiveEntryItem(entry.FullName, entry.Length, entry.CompressedLength, entry.LastWriteTime, isFolder));
                if (items.Count >= maxEntries) break;
            }
            return items;
        }, cancellationToken);

    public Task<OperationOutcome> ExtractAsync(
        string archivePath, string destinationFolder,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        // 保留旧端口的历史语义（整包、保留结构、覆盖）；v0.15 UI 一律改走
        // ExtractEntriesAsync 并在执行前向用户展示所选冲突策略，默认两者保留。
        ExtractEntriesAsync(archivePath, destinationFolder,
            new ArchiveExtractOptions(EntryFullNames: null, PreserveStructure: true, ConflictPolicy.Overwrite),
            progress, cancellationToken);

    public async Task<OperationOutcome> ExtractEntriesAsync(
        string archivePath, string destinationFolder, ArchiveExtractOptions options,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<OperationError>();
        var produced = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(destinationFolder);
                var destRoot = Path.GetFullPath(destinationFolder);
                using var archive = ZipFile.OpenRead(archivePath);
                var selectedEntries = archive.Entries
                    .Where(entry => IsEntrySelected(entry.FullName, options.EntryFullNames))
                    .ToArray();
                var total = selectedEntries.Length;
                var done = 0;

                foreach (var entry in selectedEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    done++;
                    progress?.Report(new OperationProgress(
                        total == 0 ? 1 : (double)done / total, entry.FullName, done, total));

                    var isFolder = IsFolderEntry(entry.FullName);
                    if (isFolder && !options.PreserveStructure)
                        continue; // 扁平化解压只落文件，空文件夹没有可保留的层级语义。

                    var relativeTarget = GetRelativeTarget(entry.FullName, isFolder, options.PreserveStructure);
                    if (string.IsNullOrWhiteSpace(relativeTarget))
                    {
                        errors.Add(new OperationError(entry.FullName, "条目名为空或不安全，已跳过"));
                        continue;
                    }

                    string target;
                    try
                    {
                        target = Path.GetFullPath(Path.Combine(destRoot, relativeTarget));
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
                    {
                        errors.Add(new OperationError(entry.FullName, $"文件名无法用于当前文件系统：{ex.Message}"));
                        continue;
                    }
                    if (!IsInsideDirectory(target, destRoot) || (!isFolder && string.Equals(target, destRoot, PathComparison)))
                    {
                        errors.Add(new OperationError(entry.FullName, "不安全的路径（越出解压目录），已跳过"));
                        continue;
                    }

                    try
                    {
                        if (isFolder)
                        {
                            if (File.Exists(target))
                                throw new IOException("同名文件已存在，无法创建文件夹");
                            Directory.CreateDirectory(target);
                            produced.Add(target);
                            continue;
                        }

                        var overwrite = false;
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            switch (options.ConflictPolicy)
                            {
                                case ConflictPolicy.Skip:
                                    continue;
                                case ConflictPolicy.KeepBoth:
                                    target = FindAvailableTarget(target);
                                    break;
                                case ConflictPolicy.Overwrite:
                                    if (Directory.Exists(target))
                                        throw new IOException("同名文件夹已存在，无法覆盖为文件");
                                    overwrite = true;
                                    break;
                            }
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destRoot);
                        entry.ExtractToFile(target, overwrite);
                        produced.Add(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        errors.Add(new OperationError(entry.FullName, ex.Message));
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new OperationOutcome(0, errors, produced, Cancelled: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new OperationError(Path.GetFileName(archivePath), $"无法打开或解压压缩包：{ex.Message}"));
        }

        return new OperationOutcome(produced.Count, errors, produced);
    }

    public Task<byte[]?> ReadEntryBytesAsync(
        string archivePath, string entryFullName, int maxBytes, CancellationToken cancellationToken) =>
        Task.Run<byte[]?>(() =>
        {
            if (maxBytes <= 0 || IsFolderEntry(entryFullName))
                return null;

            var limit = Math.Min(maxBytes, 64 * 1024 * 1024);
            var wanted = NormalizeEntryName(entryFullName);
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(NormalizeEntryName(candidate.FullName), wanted, StringComparison.Ordinal));
            if (entry is null || entry.Length > limit)
                return null;

            using var input = entry.Open();
            using var output = new MemoryStream(entry.Length is > 0 and <= int.MaxValue ? (int)entry.Length : 0);
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > limit)
                    return null; // 中央目录声明与实际流不一致时同样拒绝无界读取。
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }, cancellationToken);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeEntryName(string entryName) => entryName.Replace('\\', '/');

    private static bool IsFolderEntry(string entryName)
    {
        var normalized = NormalizeEntryName(entryName);
        return normalized.EndsWith('/');
    }

    private static bool IsEntrySelected(string entryFullName, IReadOnlySet<string>? selectedNames)
    {
        if (selectedNames is null) return true;
        var entry = NormalizeEntryName(entryFullName);
        foreach (var selected in selectedNames)
        {
            var wanted = NormalizeEntryName(selected);
            if (string.Equals(entry, wanted, StringComparison.Ordinal))
                return true;
            if (wanted.EndsWith('/') && entry.StartsWith(wanted, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string GetRelativeTarget(string entryFullName, bool isFolder, bool preserveStructure)
    {
        var normalized = NormalizeEntryName(entryFullName).TrimEnd('/');
        if (!preserveStructure && !isFolder)
        {
            var slash = normalized.LastIndexOf('/');
            normalized = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        }
        return normalized;
    }

    private static bool IsInsideDirectory(string target, string root)
    {
        if (string.Equals(target, root, PathComparison)) return true;
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = trimmedRoot.Length == 0
            ? Path.DirectorySeparatorChar.ToString()
            : trimmedRoot + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, PathComparison);
    }

    private static string FindAvailableTarget(string target)
    {
        var folder = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileName(target);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, PathText.KeepBothName(name, i));
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    public async Task<OperationOutcome> CompressToZipAsync(
        IReadOnlyList<string> sources, string destinationZipPath,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<OperationError>();
        try
        {
            await Task.Run(() =>
            {
                using var stream = new FileStream(destinationZipPath, FileMode.CreateNew, FileAccess.Write);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
                var done = 0;
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        AddToArchive(archive, source, Path.GetFileName(source.TrimEnd('/', '\\')), cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors.Add(new OperationError(Path.GetFileName(source), ex.Message));
                    }
                    done++;
                    progress?.Report(new OperationProgress((double)done / Math.Max(1, sources.Count), Path.GetFileName(source), done, sources.Count));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new OperationOutcome(0, errors, [], Cancelled: true);
        }

        return new OperationOutcome(1, errors, [destinationZipPath]);
    }

    private static void AddToArchive(ZipArchive archive, string sourcePath, string entryName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(sourcePath))
        {
            var children = Directory.EnumerateFileSystemEntries(sourcePath).ToList();
            if (children.Count == 0)
                archive.CreateEntry(entryName.TrimEnd('/') + "/");
            foreach (var child in children)
                AddToArchive(archive, child, entryName.TrimEnd('/') + "/" + Path.GetFileName(child), ct);
            return;
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var input = File.OpenRead(sourcePath);
        using var output = entry.Open();
        input.CopyTo(output);
    }
}
