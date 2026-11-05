using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform.Storage;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.Services;

// ---------- 存储条目接缝 ----------
// Avalonia 的 IStorageItem/IStorageFile/IStorageFolder 标注 [NotClientImplementable]
//（隐藏接口成员，编译期拒绝用户实现）。委托层只依赖本接缝：生产用适配器包装 Avalonia 条目，
// 测试以内存树实现同一接缝（无模拟器环境下验证 SAF 委托行为的唯一途径）。

/// <summary>存储条目接缝（文件/文件夹共有面）。Key 为定位串（content:// URI 等）。</summary>
public interface IStorageEntry
{
    string Name { get; }
    string Key { get; }
    bool IsFolder { get; }

    /// <summary>（字节数，修改时间）；提供者缺数据时长度为 null、时间退回 MinValue。</summary>
    Task<(long? Length, DateTimeOffset LastWrite)> GetInfoAsync();

    Task DeleteAsync();

    /// <summary>整树移动到目标夹；返回移动后的条目（Key 可能变化）。</summary>
    Task<IStorageEntry> MoveToAsync(IStorageEntryFolder destinationFolder);
}

public interface IStorageEntryFile : IStorageEntry
{
    Task<Stream> OpenReadAsync();
    Task<Stream> OpenWriteAsync();
}

public interface IStorageEntryFolder : IStorageEntry
{
    IAsyncEnumerable<IStorageEntry> GetChildrenAsync();

    /// <summary>按名取子条目（文件优先于同名夹）；不存在为 null。</summary>
    Task<IStorageEntry?> GetChildAsync(string name);

    Task<IStorageEntryFile> CreateFileAsync(string name);
    Task<IStorageEntryFolder> CreateFolderAsync(string name);
}

/// <summary>Avalonia 存储条目 → 接缝适配。</summary>
internal static class AvaloniaStorageEntry
{
    public static IStorageEntry Wrap(IStorageItem item) => item switch
    {
        IStorageFolder folder => new Folder(folder),
        IStorageFile file => new File(file),
        _ => new File(item),
    };

    private abstract class EntryBase(IStorageItem item) : IStorageEntry
    {
        protected IStorageItem Item => item;
        public string Name => item.Name;
        public string Key => item.Path.ToString();
        public abstract bool IsFolder { get; }

        public async Task<(long? Length, DateTimeOffset LastWrite)> GetInfoAsync()
        {
            var props = await item.GetBasicPropertiesAsync().ConfigureAwait(false);
            long? length = props?.Size is { } size ? (long)Math.Min(size, (ulong)long.MaxValue) : null;
            return (length, props?.DateModified ?? props?.DateCreated ?? DateTimeOffset.MinValue);
        }

        public Task DeleteAsync() => item.DeleteAsync();

        public async Task<IStorageEntry> MoveToAsync(IStorageEntryFolder destinationFolder)
        {
            if (destinationFolder is not Folder dest)
                throw new InvalidOperationException("跨提供者移动不受支持。");
            var moved = await item.MoveAsync(dest.Item).ConfigureAwait(false);
            return Wrap(moved ?? item);
        }
    }

    private sealed class File(IStorageItem item) : EntryBase(item), IStorageEntryFile
    {
        public override bool IsFolder => false;

        public Task<Stream> OpenReadAsync() =>
            Item is IStorageFile file ? file.OpenReadAsync() : throw new InvalidOperationException("不是可读文件。");

        public Task<Stream> OpenWriteAsync() =>
            Item is IStorageFile file ? file.OpenWriteAsync() : throw new InvalidOperationException("不是可写文件。");
    }

    private sealed class Folder(IStorageFolder folder) : EntryBase(folder), IStorageEntryFolder
    {
        public new IStorageFolder Item => folder;
        public override bool IsFolder => true;

        public async IAsyncEnumerable<IStorageEntry> GetChildrenAsync()
        {
            await foreach (var child in folder.GetItemsAsync().ConfigureAwait(false))
                yield return Wrap(child);
        }

        public async Task<IStorageEntry?> GetChildAsync(string name)
        {
            if (await folder.GetFileAsync(name).ConfigureAwait(false) is { } file)
                return Wrap(file);
            if (await folder.GetFolderAsync(name).ConfigureAwait(false) is { } sub)
                return Wrap(sub);
            return null;
        }

        public async Task<IStorageEntryFile> CreateFileAsync(string name)
        {
            var created = await folder.CreateFileAsync(name).ConfigureAwait(false)
                          ?? throw new IOException($"无法创建文件：{name}");
            return (IStorageEntryFile)Wrap(created);
        }

        public async Task<IStorageEntryFolder> CreateFolderAsync(string name)
        {
            var created = await folder.CreateFolderAsync(name).ConfigureAwait(false)
                          ?? await folder.GetFolderAsync(name).ConfigureAwait(false)
                          ?? throw new IOException($"无法创建文件夹：{name}");
            return (IStorageEntryFolder)Wrap(created);
        }
    }
}

/// <summary>
/// 存储条目注册表：定位串 → 接缝条目与父链。
/// 浏览器以定位串为键（FileLocator 存储形态），条目实例由此按会话缓存与复用。
/// </summary>
public sealed class StorageItemRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IStorageEntry> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);

    public void Register(IStorageEntry item, string? parentKey)
    {
        lock (_gate)
        {
            _items[item.Key] = item;
            if (parentKey is not null)
                _parents[item.Key] = parentKey;
        }
    }

    public IStorageEntry? Resolve(string key)
    {
        lock (_gate)
            return _items.GetValueOrDefault(key);
    }

    public string? ParentOf(string key)
    {
        lock (_gate)
            return _parents.GetValueOrDefault(key);
    }

    public bool Contains(string key)
    {
        lock (_gate)
            return _items.ContainsKey(key);
    }

    public void Forget(string key)
    {
        lock (_gate)
        {
            _items.Remove(key);
            _parents.Remove(key);
        }
    }

    /// <summary>移除一个授权根及其已登记后代；移除授权入口后不再从应用内解析这棵缓存树。</summary>
    public void ForgetTree(string rootKey)
    {
        lock (_gate)
        {
            var doomed = new HashSet<string>(StringComparer.Ordinal) { rootKey };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (child, parent) in _parents)
                {
                    if (doomed.Contains(parent) && doomed.Add(child))
                        changed = true;
                }
            }

            foreach (var key in doomed)
            {
                _items.Remove(key);
                _parents.Remove(key);
            }
        }
    }
}

/// <summary>
/// 双形态委托文件系统：本地路径转发 <see cref="IFileSystemService"/> 本地实现；
/// content:// 定位串走 Avalonia StorageProvider（Android SAF 授权树）。StorageProvider 在视图挂载后注入。
/// </summary>
public sealed class DelegatingFileSystemService(IFileSystemService local, StorageItemRegistry registry) : IFileSystemService
{
    public IStorageProvider? StorageProvider { get; set; }

    internal StorageItemRegistry Registry => registry;

    internal static bool IsStorage(string key) => FileLocator.IsStorageUri(key);

    /// <summary>根文件夹（选择器/书签打开结果）登记入册，返回其定位串。</summary>
    public string RegisterRoot(IStorageFolder folder) => RegisterRootEntry(AvaloniaStorageEntry.Wrap(folder));

    /// <summary>接缝根登记（测试树与未来其它提供者来源）。</summary>
    public string RegisterRootEntry(IStorageEntry folder)
    {
        registry.Register(folder, null);
        return folder.Key;
    }

    /// <summary>从会话注册表中移除授权根及后代（不触碰真实文件或系统授权本身）。</summary>
    public void ForgetRoot(string key) => registry.ForgetTree(key);

    /// <summary>
    /// 确保文件夹定位串可被委托层解析。跨重启还原 SAF 文件时，嵌套父夹可能尚未被浏览登记；
    /// 此时经 StorageProvider 按定位串重新取得文件夹。仅登记该夹本身，不猜测其父链。
    /// </summary>
    internal async Task<bool> EnsureFolderRegisteredAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsStorage(key))
            return Directory.Exists(key);
        if (ResolveFolder(key) is not null)
            return true;
        if (StorageProvider is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var folder = await StorageProvider.TryGetFolderFromPathAsync(key).ConfigureAwait(false);
            if (folder is null)
                return false;
            registry.Register(AvaloniaStorageEntry.Wrap(folder), parentKey: null);
            return ResolveFolder(key) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    internal IStorageEntry? ResolveItem(string key) => registry.Resolve(key);

    internal IStorageEntryFolder? ResolveFolder(string key) => registry.Resolve(key) as IStorageEntryFolder;

    public async IAsyncEnumerable<FileItem> ListAsync(
        string folderPath, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsStorage(folderPath))
        {
            await foreach (var item in local.ListAsync(folderPath, includeHidden, cancellationToken).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        var folder = ResolveFolder(folderPath)
                     ?? throw new DirectoryNotFoundException("授权文件夹不可用，可能授权已被系统撤销。");
        await foreach (var child in folder.GetChildrenAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            registry.Register(child, folderPath);
            var hidden = child.Name.StartsWith('.');
            if (hidden && !includeHidden) continue;
            yield return await ToFileItemAsync(child).ConfigureAwait(false);
        }
    }

    public async Task<FileItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsStorage(path))
            return await local.StatAsync(path, cancellationToken).ConfigureAwait(false);
        var item = ResolveItem(path);
        return item is null ? null : await ToFileItemAsync(item).ConfigureAwait(false);
    }

    private static async Task<FileItem> ToFileItemAsync(IStorageEntry item)
    {
        (long? Length, DateTimeOffset LastWrite) info = (null, DateTimeOffset.MinValue);
        try { info = await item.GetInfoAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }

        return new FileItem(
            FileLocator.FromStorage(item.Key),
            item.Name,
            item.IsFolder ? FileKind.Folder : FileKindClassifier.Classify(item.Name),
            info.Length,
            info.LastWrite,
            item.Name.StartsWith('.'),
            IsSymlink: false,
            item.IsFolder ? null : Path.GetExtension(item.Name));
    }

    public async Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        if (!IsStorage(parentPath))
            return await local.CreateFolderAsync(parentPath, name, cancellationToken).ConfigureAwait(false);
        var parent = ResolveFolder(parentPath) ?? throw new DirectoryNotFoundException("授权文件夹不可用。");
        var created = await parent.CreateFolderAsync(name).ConfigureAwait(false);
        registry.Register(created, parentPath);
        return created.Key;
    }

    public async Task<string> CreateFileAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        if (!IsStorage(parentPath))
            return await local.CreateFileAsync(parentPath, name, cancellationToken).ConfigureAwait(false);
        var parent = ResolveFolder(parentPath) ?? throw new DirectoryNotFoundException("授权文件夹不可用。");
        var created = await parent.CreateFileAsync(name).ConfigureAwait(false);
        registry.Register(created, parentPath);
        return created.Key;
    }

    public async Task<string> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken)
    {
        if (!IsStorage(sourcePath))
            return await local.RenameAsync(sourcePath, newName, cancellationToken).ConfigureAwait(false);

        // StorageProvider 无原生改名：同夹复制（夹则递归）后删源。v1 已知代价，文档在册。
        _ = ResolveItem(sourcePath) ?? throw new FileNotFoundException("项目不可用。", sourcePath);
        var parentKey = registry.ParentOf(sourcePath) ?? throw new IOException("无法确定父文件夹。");
        var produced = await CopyItemAsync(sourcePath, parentKey, newName, cancellationToken).ConfigureAwait(false);
        await DeleteKeyAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return produced;
    }

    public bool Exists(string path) => IsStorage(path) ? registry.Contains(path) : local.Exists(path);

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsStorage(path))
            return await local.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var file = ResolveItem(path) as IStorageEntryFile ?? throw new FileNotFoundException("文件不可用。", path);
        return await file.OpenReadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 存储形态的覆盖写入。提供者没有原子替换语义：写入即就地截断，
    /// 调用方（文本写回）必须自行复核结果并准备好恢复原内容。
    /// </summary>
    public async Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsStorage(path))
            return await local.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (ResolveItem(path) is not IStorageEntryFile file)
            throw new NotSupportedException("授权夹内只有文件可以写回；文件夹与未登记条目不受支持。");
        return await file.OpenWriteAsync().ConfigureAwait(false);
    }

    public string? GetParentPath(string path) =>
        IsStorage(path) ? registry.ParentOf(path) : local.GetParentPath(path);

    public IReadOnlyList<PathSegment> GetBreadcrumbs(string path)
    {
        if (!IsStorage(path))
            return local.GetBreadcrumbs(path);
        var segments = new List<PathSegment>();
        string? current = path;
        while (current is not null)
        {
            var item = ResolveItem(current);
            segments.Add(new PathSegment(item?.Name ?? current, current));
            current = registry.ParentOf(current);
        }
        segments.Reverse();
        return segments;
    }

    // ---------- 供委托操作服务复用的原语（双形态统一） ----------

    internal async Task<IStorageEntry?> ResolveStorageChildAsync(string folderKey, string name)
    {
        var folder = ResolveFolder(folderKey);
        if (folder is null) return null;
        var child = await folder.GetChildAsync(name).ConfigureAwait(false);
        if (child is not null)
            registry.Register(child, folderKey);
        return child;
    }

    internal async Task<bool> ChildExistsAsync(string folderKey, string name)
    {
        if (!IsStorage(folderKey))
        {
            var full = Path.Combine(folderKey, name);
            return File.Exists(full) || Directory.Exists(full);
        }
        return await ResolveStorageChildAsync(folderKey, name).ConfigureAwait(false) is not null;
    }

    internal async Task<string?> ChildKeyAsync(string folderKey, string name)
    {
        if (!IsStorage(folderKey))
        {
            var full = Path.Combine(folderKey, name);
            return File.Exists(full) || Directory.Exists(full) ? full : null;
        }
        return (await ResolveStorageChildAsync(folderKey, name).ConfigureAwait(false))?.Key;
    }

    internal Task<string> CreateFolderDirectAsync(string parentKey, string name, CancellationToken ct) =>
        CreateFolderAsync(parentKey, name, ct);

    /// <summary>在目标夹中建文件并打开写流（存储形态下先删同名文件以保证截断语义）。返回定位串。</summary>
    internal async Task<(Stream Stream, string Key)> OpenWriteInFolderAsync(string folderKey, string name, CancellationToken ct)
    {
        if (!IsStorage(folderKey))
        {
            var full = Path.Combine(folderKey, name);
            return (new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true), full);
        }
        var folder = ResolveFolder(folderKey) ?? throw new DirectoryNotFoundException("授权文件夹不可用。");
        if (await folder.GetChildAsync(name).ConfigureAwait(false) is IStorageEntryFile existing)
            await existing.DeleteAsync().ConfigureAwait(false);
        var file = await folder.CreateFileAsync(name).ConfigureAwait(false);
        registry.Register(file, folderKey);
        return (await file.OpenWriteAsync().ConfigureAwait(false), file.Key);
    }

    internal bool IsFolderKey(string key) =>
        IsStorage(key) ? ResolveItem(key)?.IsFolder ?? false : Directory.Exists(key);

    internal string DisplayNameOf(string key) =>
        IsStorage(key) ? ResolveItem(key)?.Name ?? key : Path.GetFileName(key.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>把任一形态的条目流式复制到目标夹并命名（夹递归）。返回产出定位串。</summary>
    internal async Task<string> CopyItemAsync(string sourceKey, string destFolderKey, string destName, CancellationToken ct)
    {
        if (!IsFolderKey(sourceKey))
        {
            await using var source = await OpenReadAsync(sourceKey, ct).ConfigureAwait(false);
            var (stream, producedKey) = await OpenWriteInFolderAsync(destFolderKey, destName, ct).ConfigureAwait(false);
            await using (stream)
                await source.CopyToAsync(stream, ct).ConfigureAwait(false);
            return producedKey;
        }

        var newFolderKey = await CreateFolderDirectAsync(destFolderKey, destName, ct).ConfigureAwait(false);
        foreach (var (childKey, childName) in await ListChildrenKeysAsync(sourceKey, ct).ConfigureAwait(false))
            await CopyItemAsync(childKey, newFolderKey, childName, ct).ConfigureAwait(false);
        return newFolderKey;
    }

    internal async Task<IReadOnlyList<(string Key, string Name)>> ListChildrenKeysAsync(string folderKey, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        if (!IsStorage(folderKey))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folderKey))
            {
                ct.ThrowIfCancellationRequested();
                result.Add((entry, Path.GetFileName(entry)));
            }
            return result;
        }
        var folder = ResolveFolder(folderKey) ?? throw new DirectoryNotFoundException("授权文件夹不可用。");
        await foreach (var child in folder.GetChildrenAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            registry.Register(child, folderKey);
            result.Add((child.Key, child.Name));
        }
        return result;
    }

    /// <summary>递归删除任一形态条目（存储文件夹先清子项再删本体，规避提供者差异）。</summary>
    internal async Task DeleteKeyAsync(string key, CancellationToken ct)
    {
        if (!IsStorage(key))
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(key)) Directory.Delete(key, recursive: true);
                else if (File.Exists(key)) File.Delete(key);
            }, ct).ConfigureAwait(false);
            return;
        }
        var item = ResolveItem(key) ?? throw new FileNotFoundException("项目不可用。", key);
        if (item.IsFolder)
        {
            foreach (var (childKey, _) in await ListChildrenKeysAsync(key, ct).ConfigureAwait(false))
                await DeleteKeyAsync(childKey, ct).ConfigureAwait(false);
        }
        await item.DeleteAsync().ConfigureAwait(false);
        registry.Forget(key);
    }
}

/// <summary>
/// 双形态委托操作服务：整批与目标皆本地时原样转发既有引擎（桌面零行为变化）；
/// 含存储形态时走统一流复制引擎（同树移动优先接缝 MoveToAsync，冲突策略与 §5.5 对齐）。
/// </summary>
public sealed class DelegatingFileOperationService(
    IFileOperationService local,
    DelegatingFileSystemService fileSystem) : IFileOperationService
{
    public Task<OperationOutcome> CopyAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        AllLocal(sources, destinationFolder)
            ? local.CopyAsync(sources, destinationFolder, policy, progress, cancellationToken)
            : TransferAsync(sources, destinationFolder, policy, isMove: false, progress, cancellationToken);

    public Task<OperationOutcome> MoveAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        AllLocal(sources, destinationFolder)
            ? local.MoveAsync(sources, destinationFolder, policy, progress, cancellationToken)
            : TransferAsync(sources, destinationFolder, policy, isMove: true, progress, cancellationToken);

    public async Task<OperationOutcome> DeletePermanentlyAsync(
        IReadOnlyList<string> sources,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (sources.All(s => !DelegatingFileSystemService.IsStorage(s)))
            return await local.DeletePermanentlyAsync(sources, progress, cancellationToken).ConfigureAwait(false);

        var errors = new List<OperationError>();
        var done = 0;
        foreach (var source in sources)
        {
            if (cancellationToken.IsCancellationRequested)
                return new OperationOutcome(done, errors, [], Cancelled: true);
            try
            {
                await fileSystem.DeleteKeyAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new OperationOutcome(done, errors, [], Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                errors.Add(new OperationError(fileSystem.DisplayNameOf(source), ex.Message, source));
            }
            done++;
            progress?.Report(new OperationProgress((double)done / Math.Max(1, sources.Count),
                fileSystem.DisplayNameOf(source), done, sources.Count));
        }
        return new OperationOutcome(done - errors.Count, errors, []);
    }

    private static bool AllLocal(IReadOnlyList<string> sources, string destinationFolder) =>
        !DelegatingFileSystemService.IsStorage(destinationFolder)
        && sources.All(s => !DelegatingFileSystemService.IsStorage(s));

    private async Task<OperationOutcome> TransferAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy, bool isMove,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var errors = new List<OperationError>();
        var produced = new List<string>();
        var done = 0;
        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested)
                return new OperationOutcome(done, errors, produced, Cancelled: true);
            var name = fileSystem.DisplayNameOf(source);
            try
            {
                var parent = fileSystem.GetParentPath(source);
                if (isMove && parent is not null && string.Equals(parent, destinationFolder, StringComparison.Ordinal))
                {
                    done++;
                    continue; // 移到自身目录视为无操作（与本地引擎一致）
                }

                var targetName = name;
                var exists = await fileSystem.ChildExistsAsync(destinationFolder, targetName).ConfigureAwait(false);
                var sameFolder = parent is not null && string.Equals(parent, destinationFolder, StringComparison.Ordinal);
                if (exists && (policy == ConflictPolicy.KeepBoth || (sameFolder && policy == ConflictPolicy.Overwrite)))
                {
                    var i = 1;
                    do { targetName = PathText.KeepBothName(name, i++); }
                    while (await fileSystem.ChildExistsAsync(destinationFolder, targetName).ConfigureAwait(false));
                    exists = false;
                }
                if (exists && policy == ConflictPolicy.Skip)
                {
                    done++;
                    continue;
                }
                if (exists && policy == ConflictPolicy.Overwrite)
                {
                    if (await fileSystem.ChildKeyAsync(destinationFolder, targetName).ConfigureAwait(false) is { } victim)
                        await fileSystem.DeleteKeyAsync(victim, ct).ConfigureAwait(false);
                }

                string producedKey;
                var bothStorage = DelegatingFileSystemService.IsStorage(source)
                                  && DelegatingFileSystemService.IsStorage(destinationFolder);
                if (isMove && bothStorage && targetName == name
                    && fileSystem.ResolveItem(source) is { } item
                    && fileSystem.ResolveFolder(destinationFolder) is { } destFolder)
                {
                    var moved = await item.MoveToAsync(destFolder).ConfigureAwait(false);
                    fileSystem.Registry.Forget(source);
                    fileSystem.Registry.Register(moved, destinationFolder);
                    producedKey = moved.Key;
                }
                else
                {
                    producedKey = await fileSystem.CopyItemAsync(source, destinationFolder, targetName, ct).ConfigureAwait(false);
                    if (isMove)
                        await fileSystem.DeleteKeyAsync(source, ct).ConfigureAwait(false);
                }
                produced.Add(producedKey);
            }
            catch (OperationCanceledException)
            {
                return new OperationOutcome(done, errors, produced, Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                errors.Add(new OperationError(fileSystem.DisplayNameOf(source), ex.Message, source));
            }
            done++;
            progress?.Report(new OperationProgress((double)done / Math.Max(1, sources.Count),
                fileSystem.DisplayNameOf(source), done, sources.Count));
        }
        return new OperationOutcome(done - errors.Count, errors, produced);
    }
}

/// <summary>已授权文件夹（书签）的持久化（storage-roots.json，源生成 JSON 与设置同风格）。</summary>
public sealed record AuthorizedRoot(string Name, string Bookmark);

public static class AuthorizedRootsStore
{
    public static IReadOnlyList<AuthorizedRoot> Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "storage-roots.json");
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, AuthorizedRootsJsonContext.Default.ListAuthorizedRoot) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static async Task SaveAsync(string dataDirectory, IReadOnlyList<AuthorizedRoot> roots)
    {
        var path = Path.Combine(dataDirectory, "storage-roots.json");
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(roots.ToList(), AuthorizedRootsJsonContext.Default.ListAuthorizedRoot);
        await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSerializable(typeof(List<AuthorizedRoot>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AuthorizedRootsJsonContext : JsonSerializerContext { }
