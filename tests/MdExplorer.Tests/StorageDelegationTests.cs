using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.UI.Services;
using Xunit;

namespace MdExplorer.Tests;

// 存储接缝的内存树替身（模拟 Android SAF 授权树）：Avalonia 存储接口标注 NotClientImplementable
// 不可自实现，故委托层依赖 IStorageEntry 接缝、测试实现同一接缝（无模拟器环境下验证委托行为）。
internal sealed class FakeEntryFile(string name, string key, FakeEntryFolder? parent) : IStorageEntryFile
{
    public byte[] Content = [];
    public FakeEntryFolder? Parent { get; set; } = parent;
    public string Name { get; } = name;
    public string Key { get; set; } = key;
    public bool IsFolder => false;

    public Task<(long? Length, DateTimeOffset LastWrite)> GetInfoAsync() =>
        Task.FromResult<(long?, DateTimeOffset)>((Content.Length, DateTimeOffset.UnixEpoch));

    public Task DeleteAsync()
    {
        Parent?.Children.Remove(this);
        return Task.CompletedTask;
    }

    public Task<IStorageEntry> MoveToAsync(IStorageEntryFolder destinationFolder)
    {
        if (destinationFolder is FakeEntryFolder dest)
        {
            Parent?.Children.Remove(this);
            Parent = dest;
            dest.Children.Add(this);
            Key = dest.Key + "/" + Name;
        }
        return Task.FromResult<IStorageEntry>(this);
    }

    public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new MemoryStream(Content, writable: false));
    public Task<Stream> OpenWriteAsync() => Task.FromResult<Stream>(new CommitStream(this));

    private sealed class CommitStream(FakeEntryFile owner) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.Content = ToArray();
            base.Dispose(disposing);
        }
    }
}

internal sealed class FakeEntryFolder(string name, string key, FakeEntryFolder? parent) : IStorageEntryFolder
{
    public List<IStorageEntry> Children { get; } = [];
    public FakeEntryFolder? Parent { get; set; } = parent;
    public string Name { get; } = name;
    public string Key { get; set; } = key;
    public bool IsFolder => true;

    public FakeEntryFile AddFile(string name, string content)
    {
        var file = new FakeEntryFile(name, Key + "/" + name, this) { Content = Encoding.UTF8.GetBytes(content) };
        Children.Add(file);
        return file;
    }

    public FakeEntryFolder AddFolder(string name)
    {
        var folder = new FakeEntryFolder(name, Key + "/" + name, this);
        Children.Add(folder);
        return folder;
    }

    public Task<(long? Length, DateTimeOffset LastWrite)> GetInfoAsync() =>
        Task.FromResult<(long?, DateTimeOffset)>((null, DateTimeOffset.UnixEpoch));

    public Task DeleteAsync()
    {
        Parent?.Children.Remove(this);
        return Task.CompletedTask;
    }

    public Task<IStorageEntry> MoveToAsync(IStorageEntryFolder destinationFolder)
    {
        if (destinationFolder is FakeEntryFolder dest)
        {
            Parent?.Children.Remove(this);
            Parent = dest;
            dest.Children.Add(this);
            Key = dest.Key + "/" + Name;
        }
        return Task.FromResult<IStorageEntry>(this);
    }

    public async IAsyncEnumerable<IStorageEntry> GetChildrenAsync()
    {
        foreach (var child in Children.ToArray())
        {
            yield return child;
            await Task.Yield();
        }
    }

    public Task<IStorageEntry?> GetChildAsync(string name) =>
        Task.FromResult(Children.FirstOrDefault(c => c.Name == name));

    public Task<IStorageEntryFile> CreateFileAsync(string name) =>
        Task.FromResult<IStorageEntryFile>(AddFile(name, ""));

    public Task<IStorageEntryFolder> CreateFolderAsync(string name) =>
        Task.FromResult<IStorageEntryFolder>(AddFolder(name));
}

public sealed class StorageDelegationTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("mdexplorer-saf-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Tree(
        DelegatingFileSystemService Fs,
        DelegatingFileOperationService Ops,
        FakeEntryFolder Root,
        string RootKey,
        string AKey,
        string SubKey);

    private static Tree BuildTree()
    {
        var fs = new DelegatingFileSystemService(new LocalFileSystemService(), new StorageItemRegistry());
        var ops = new DelegatingFileOperationService(new FileOperationService(), fs);
        var root = new FakeEntryFolder("授权夹", "content://tree/root", null);
        var a = root.AddFile("a.txt", "hello");
        root.AddFile(".hidden", "x");
        var sub = root.AddFolder("sub");
        sub.AddFile("b.txt", "world!");
        var rootKey = fs.RegisterRootEntry(root);
        return new Tree(fs, ops, root, rootKey, a.Key, sub.Key);
    }

    private static async Task<List<FileItem>> ListAllAsync(DelegatingFileSystemService fs, string key, bool hidden = false)
    {
        var items = new List<FileItem>();
        await foreach (var item in fs.ListAsync(key, hidden, CancellationToken.None))
            items.Add(item);
        return items;
    }

    private static async Task<string> ReadTextAsync(DelegatingFileSystemService fs, string key)
    {
        await using var stream = await fs.OpenReadAsync(key, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task ListsStorageTreeWithKindsSizesAndHiddenFilter()
    {
        var t = BuildTree();
        var items = await ListAllAsync(t.Fs, t.RootKey);
        Assert.Equal(2, items.Count); // .hidden 被过滤
        var a = Assert.Single(items, i => i.Name == "a.txt");
        Assert.Equal(FileKind.Text, a.Kind);
        Assert.Equal(5, a.Length);
        Assert.True(a.Locator.IsStorage);
        Assert.Equal(FileKind.Folder, Assert.Single(items, i => i.Name == "sub").Kind);
        Assert.Equal(3, (await ListAllAsync(t.Fs, t.RootKey, hidden: true)).Count);
        Assert.Equal("hello", await ReadTextAsync(t.Fs, a.FullPath));
    }

    [Fact]
    public async Task BreadcrumbsAndParentsFollowRegistryChain()
    {
        var t = BuildTree();
        _ = await ListAllAsync(t.Fs, t.RootKey);
        Assert.Null(t.Fs.GetParentPath(t.RootKey));
        Assert.Equal(t.RootKey, t.Fs.GetParentPath(t.SubKey));
        Assert.Equal(new[] { "授权夹", "sub" }, t.Fs.GetBreadcrumbs(t.SubKey).Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task CopiesStorageFolderToLocalRecursively()
    {
        var t = BuildTree();
        _ = await ListAllAsync(t.Fs, t.RootKey);
        var outcome = await t.Ops.CopyAsync([t.SubKey], _tempDir, ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.True(outcome.Success, string.Join(";", outcome.Errors.Select(e => e.Message)));
        Assert.Equal("world!", await File.ReadAllTextAsync(Path.Combine(_tempDir, "sub", "b.txt")));
    }

    [Fact]
    public async Task CopiesLocalFileIntoStorage()
    {
        var t = BuildTree();
        var localFile = Path.Combine(_tempDir, "note.md");
        await File.WriteAllTextAsync(localFile, "# 标题");
        var outcome = await t.Ops.CopyAsync([localFile], t.RootKey, ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.True(outcome.Success, string.Join(";", outcome.Errors.Select(e => e.Message)));
        var produced = Assert.Single(outcome.ProducedPaths);
        Assert.Equal("# 标题", await ReadTextAsync(t.Fs, produced));
        var stat = await t.Fs.StatAsync(produced, CancellationToken.None);
        Assert.Equal("note.md", stat?.Name);
    }

    [Fact]
    public async Task MovesWithinStorageTreeUsingProviderMove()
    {
        var t = BuildTree();
        _ = await ListAllAsync(t.Fs, t.RootKey);
        var outcome = await t.Ops.MoveAsync([t.AKey], t.SubKey, ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.True(outcome.Success, string.Join(";", outcome.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(t.Root.Children, c => c.Name == "a.txt");
        var produced = Assert.Single(outcome.ProducedPaths);
        Assert.Equal(t.SubKey, t.Fs.GetParentPath(produced));
        Assert.Equal("hello", await ReadTextAsync(t.Fs, produced));
    }

    [Fact]
    public async Task RenamesStorageFileByCopyAndDelete()
    {
        var t = BuildTree();
        _ = await ListAllAsync(t.Fs, t.RootKey);
        var newKey = await t.Fs.RenameAsync(t.AKey, "a2.txt", CancellationToken.None);
        Assert.Equal("hello", await ReadTextAsync(t.Fs, newKey));
        Assert.False(t.Fs.Exists(t.AKey));
        Assert.Contains(await ListAllAsync(t.Fs, t.RootKey), i => i.Name == "a2.txt");
    }

    [Fact]
    public async Task DeletesStorageFolderRecursively()
    {
        var t = BuildTree();
        _ = await ListAllAsync(t.Fs, t.RootKey);
        var outcome = await t.Ops.DeletePermanentlyAsync([t.SubKey], null, CancellationToken.None);
        Assert.True(outcome.Success, string.Join(";", outcome.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(t.Root.Children, c => c.Name == "sub");
    }

    [Fact]
    public async Task ForgetRootDropsRegisteredDescendantsWithoutDeletingProviderTree()
    {
        var t = BuildTree();
        _ = await ListAllAsync(t.Fs, t.RootKey);
        var subItems = await ListAllAsync(t.Fs, t.SubKey);
        var nestedKey = Assert.Single(subItems).FullPath;

        t.Fs.ForgetRoot(t.RootKey);

        Assert.Null(await t.Fs.StatAsync(t.RootKey, CancellationToken.None));
        Assert.Null(await t.Fs.StatAsync(t.AKey, CancellationToken.None));
        Assert.Null(await t.Fs.StatAsync(nestedKey, CancellationToken.None));
        Assert.Equal(3, t.Root.Children.Count); // 只遗忘 Eidoria 的会话注册，不触碰提供者文件
    }

    [Fact]
    public async Task LocalPathsPassThroughUnchanged()
    {
        var t = BuildTree();
        var localFile = Path.Combine(_tempDir, "x.txt");
        await File.WriteAllTextAsync(localFile, "l");
        var items = await ListAllAsync(t.Fs, _tempDir);
        Assert.Contains(items, i => i.Name == "x.txt" && !i.Locator.IsStorage);
        Assert.Equal(_tempDir, t.Fs.GetParentPath(localFile));
        Assert.False(FileLocator.IsStorageUri(localFile));
        Assert.True(FileLocator.IsStorageUri(t.RootKey));
    }
}
