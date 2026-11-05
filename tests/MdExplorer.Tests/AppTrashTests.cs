using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.UI.Services;
using Xunit;

namespace MdExplorer.Tests;

public sealed class AppTrashTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("mdexplorer-apptrash-").FullName;
    private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private string DataDir => Path.Combine(_tempDir, "data");
    private string SourceDir => Directory.CreateDirectory(Path.Combine(_tempDir, "source")).FullName;

    private AppTrashService CreateService(
        DelegatingFileSystemService fileSystem,
        long quotaBytes = 1024 * 1024,
        int retentionDays = AppTrashService.DefaultRetentionDays) =>
        new(DataDir, fileSystem, quotaBytes, retentionDays, () => _now);

    private static DelegatingFileSystemService CreateFileSystem() =>
        new(new LocalFileSystemService(), new StorageItemRegistry());

    private sealed record StorageTree(
        DelegatingFileSystemService FileSystem,
        FakeEntryFolder Root,
        string RootKey,
        string FileKey,
        string FolderKey);

    private static StorageTree BuildStorageTree()
    {
        var fileSystem = CreateFileSystem();
        var root = new FakeEntryFolder("授权夹", "content://tree/root", null);
        var file = root.AddFile("a.txt", "hello");
        var folder = root.AddFolder("sub");
        folder.AddFile("b.txt", "world!");
        var rootKey = fileSystem.RegisterRootEntry(root);
        return new StorageTree(fileSystem, root, rootKey, file.Key, folder.Key);
    }

    private static async Task<List<FileItem>> ListAllAsync(DelegatingFileSystemService fileSystem, string key)
    {
        var items = new List<FileItem>();
        await foreach (var item in fileSystem.ListAsync(key, includeHidden: true, CancellationToken.None))
            items.Add(item);
        return items;
    }

    private static async Task<string> ReadStorageTextAsync(DelegatingFileSystemService fileSystem, string key)
    {
        await using var stream = await fileSystem.OpenReadAsync(key, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task LocalFileCanBeTrashedRestoredAndPermanentlyDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(CreateFileSystem());
        var source = Path.Combine(SourceDir, "note.txt");
        await File.WriteAllTextAsync(source, "hello", ct);

        await service.MoveToTrashAsync(source, ct);

        Assert.False(File.Exists(source));
        var trashed = Assert.Single(await service.ListAsync(ct));
        Assert.Equal("note.txt", trashed.OriginalName);
        Assert.Equal(source, trashed.OriginalPath);
        Assert.Equal(5, trashed.SizeBytes);
        var stats = await service.GetStatsAsync(ct);
        Assert.Equal(5, stats.UsedBytes);
        Assert.Equal(1, stats.ItemCount);
        Assert.Equal(1024 * 1024, stats.QuotaBytes);
        Assert.Equal(30, stats.RetentionDays);

        await service.RestoreAsync(trashed.Id, ct);
        Assert.Equal("hello", await File.ReadAllTextAsync(source, ct));
        Assert.Empty(await service.ListAsync(ct));

        await service.MoveToTrashAsync(source, ct);
        trashed = Assert.Single(await service.ListAsync(ct));
        await service.DeletePermanentlyAsync(trashed.Id, ct);
        Assert.False(File.Exists(source));
        Assert.Empty(await service.ListAsync(ct));
    }

    [Fact]
    public async Task LocalFolderIsTrashedAndRestoredRecursively()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(CreateFileSystem());
        var folder = Path.Combine(SourceDir, "album");
        var nested = Directory.CreateDirectory(Path.Combine(folder, "2026")).FullName;
        var file = Path.Combine(nested, "photo.txt");
        await File.WriteAllTextAsync(file, "photo-bytes", ct);

        await service.MoveToTrashAsync(folder, ct);
        var trashed = Assert.Single(await service.ListAsync(ct));
        Assert.True(trashed.IsFolder);
        Assert.Equal(11, trashed.SizeBytes);
        Assert.False(Directory.Exists(folder));

        await service.RestoreAsync(trashed.Id, ct);
        Assert.Equal("photo-bytes", await File.ReadAllTextAsync(file, ct));
    }

    [Fact]
    public async Task StorageFileCanBeCopiedIntoAppTrashAndRestored()
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = BuildStorageTree();
        _ = await ListAllAsync(tree.FileSystem, tree.RootKey);
        var service = CreateService(tree.FileSystem);

        await service.MoveToTrashAsync(tree.FileKey, ct);

        Assert.DoesNotContain(tree.Root.Children, child => child.Name == "a.txt");
        var trashed = Assert.Single(await service.ListAsync(ct));
        Assert.Equal(tree.FileKey, trashed.OriginalPath);
        Assert.Equal(5, trashed.SizeBytes);

        await service.RestoreAsync(trashed.Id, ct);
        Assert.Contains(tree.Root.Children, child => child.Name == "a.txt");
        Assert.Equal("hello", await ReadStorageTextAsync(tree.FileSystem, tree.FileKey));
        Assert.Empty(await service.ListAsync(ct));
    }

    [Fact]
    public async Task StorageFolderIsTrashedAndRestoredRecursively()
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = BuildStorageTree();
        _ = await ListAllAsync(tree.FileSystem, tree.RootKey);
        var service = CreateService(tree.FileSystem);

        await service.MoveToTrashAsync(tree.FolderKey, ct);
        var trashed = Assert.Single(await service.ListAsync(ct));
        Assert.True(trashed.IsFolder);
        Assert.Equal(6, trashed.SizeBytes);
        Assert.DoesNotContain(tree.Root.Children, child => child.Name == "sub");

        await service.RestoreAsync(trashed.Id, ct);
        var restoredChildren = await ListAllAsync(tree.FileSystem, tree.FolderKey);
        var restoredFile = Assert.Single(restoredChildren, child => child.Name == "b.txt");
        Assert.Equal("world!", await ReadStorageTextAsync(tree.FileSystem, restoredFile.FullPath));
    }

    [Fact]
    public async Task QuotaRejectsLocalAndStorageItemsWithoutRemovingSources()
    {
        var ct = TestContext.Current.CancellationToken;
        var localService = CreateService(CreateFileSystem(), quotaBytes: 4);
        var localSource = Path.Combine(SourceDir, "large.txt");
        await File.WriteAllTextAsync(localSource, "hello", ct);

        await Assert.ThrowsAsync<IOException>(() => localService.MoveToTrashAsync(localSource, ct));
        Assert.Equal("hello", await File.ReadAllTextAsync(localSource, ct));
        Assert.Empty(await localService.ListAsync(ct));

        var tree = BuildStorageTree();
        _ = await ListAllAsync(tree.FileSystem, tree.RootKey);
        var storageService = new AppTrashService(
            Path.Combine(_tempDir, "storage-data"), tree.FileSystem, quotaBytes: 4, clock: () => _now);
        await Assert.ThrowsAsync<IOException>(() => storageService.MoveToTrashAsync(tree.FileKey, ct));
        Assert.Contains(tree.Root.Children, child => child.Name == "a.txt");
        Assert.Empty(await storageService.ListAsync(ct));
    }

    /// <summary>v0.29：配额与保留期改成可在设置页调整的运行期值，改完必须立刻影响准入与统计。</summary>
    [Fact]
    public async Task QuotaAndRetentionAreMutableAtRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(CreateFileSystem(), quotaBytes: 4);
        var small = Path.Combine(SourceDir, "small.txt");
        await File.WriteAllTextAsync(small, "hello", ct);
        await Assert.ThrowsAsync<IOException>(() => service.MoveToTrashAsync(small, ct));
        Assert.Equal(4, (await service.GetStatsAsync(ct)).QuotaBytes);

        // 放宽配额后同一次删除就能通过
        service.QuotaBytes = 1024 * 1024;
        await service.MoveToTrashAsync(small, ct);
        var stats = await service.GetStatsAsync(ct);
        Assert.Equal(1024 * 1024, stats.QuotaBytes);
        Assert.Equal(1, stats.ItemCount);

        // 保留期同样可改：30 天时不算过期，改成 1 天并推进时钟后同一批条目被清掉
        service.RetentionDays = 30;
        Assert.Equal(1, (await service.GetStatsAsync(ct)).ItemCount);
        service.RetentionDays = 1;
        _now = _now.AddDays(2);
        var purged = await service.GetStatsAsync(ct);
        Assert.Equal(0, purged.ItemCount);
        Assert.Equal(1, purged.RetentionDays);

        // 非正值被拒绝，避免运行时把配额/保留期写成 0 导致全员过期或全部拒收
        Assert.Throws<ArgumentOutOfRangeException>(() => service.QuotaBytes = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.RetentionDays = 0);
        Assert.Equal(1024 * 1024, service.QuotaBytes);
        Assert.Equal(1, service.RetentionDays);
    }

    [Fact]
    public async Task RestoreDoesNotOverwriteNameConflictAndKeepsTrashCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(CreateFileSystem());
        var source = Path.Combine(SourceDir, "report.txt");
        await File.WriteAllTextAsync(source, "old", ct);
        await service.MoveToTrashAsync(source, ct);
        var trashed = Assert.Single(await service.ListAsync(ct));
        await File.WriteAllTextAsync(source, "new", ct);

        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(trashed.Id, ct));

        Assert.Equal("new", await File.ReadAllTextAsync(source, ct));
        Assert.Single(await service.ListAsync(ct));
    }

    [Fact]
    public async Task ExpiredEntriesArePurgedFromListAndStats()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(CreateFileSystem(), retentionDays: 30);
        var source = Path.Combine(SourceDir, "old.txt");
        await File.WriteAllTextAsync(source, "old", ct);
        await service.MoveToTrashAsync(source, ct);
        Assert.Single(await service.ListAsync(ct));

        _now = _now.AddDays(31);

        Assert.Empty(await service.ListAsync(ct));
        var stats = await service.GetStatsAsync(ct);
        Assert.Equal(0, stats.UsedBytes);
        Assert.Equal(0, stats.ItemCount);
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task RestoreFailsWhenStorageParentRegistrationIsGoneAndKeepsTrashCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = BuildStorageTree();
        _ = await ListAllAsync(tree.FileSystem, tree.RootKey);
        var service = CreateService(tree.FileSystem);
        await service.MoveToTrashAsync(tree.FileKey, ct);
        var trashed = Assert.Single(await service.ListAsync(ct));

        tree.FileSystem.ForgetRoot(tree.RootKey);

        var error = await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(trashed.Id, ct));
        Assert.Contains("重新授权", error.Message);
        Assert.Single(await service.ListAsync(ct));
        Assert.Contains(tree.Root.Children, child => child.Name == "sub"); // 遗忘注册不删除真实树
    }

    [Fact]
    public async Task EmptyRemovesAllEntriesAndRejectsTraversalIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(CreateFileSystem());
        var first = Path.Combine(SourceDir, "one.txt");
        var second = Path.Combine(SourceDir, "two.txt");
        await File.WriteAllTextAsync(first, "1", ct);
        await File.WriteAllTextAsync(second, "2", ct);
        await service.MoveToTrashAsync(first, ct);
        await service.MoveToTrashAsync(second, ct);
        Assert.Equal(2, (await service.ListAsync(ct)).Count);

        await service.EmptyAsync(ct);

        Assert.Empty(await service.ListAsync(ct));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeletePermanentlyAsync("../outside", ct));
    }

    [Fact]
    public async Task AuthorizedRootItselfCannotBeTrashed()
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = BuildStorageTree();
        var service = CreateService(tree.FileSystem);

        await Assert.ThrowsAsync<IOException>(() => service.MoveToTrashAsync(tree.RootKey, ct));

        Assert.Equal(2, tree.Root.Children.Count);
        Assert.Empty(await service.ListAsync(ct));
    }
}
