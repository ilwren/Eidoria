using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.30：授权夹（SAF）文本写回。这里验证的是「先复核、就地覆盖、写完再读回复核」的语义，
/// 以及失败时如实抛出而不是假装原子成功。真实设备上的 ContentResolver 行为仍须真机验收。
/// </summary>
public sealed class StorageTextFileTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("mdexplorer-saf-text-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Tree(DelegatingFileSystemService Fs, FakeEntryFolder Root, FakeEntryFile File);

    /// <summary>
    /// 委托层只在「枚举过该文件夹」后才认识子条目（与真实 SAF 一致），
    /// 所以测试也必须先登记根、再走一次 ListAsync，否则 StatAsync 会合理地返回 null。
    /// </summary>
    private static async Task RegisterTreeAsync(DelegatingFileSystemService fs, FakeEntryFolder root, CancellationToken ct)
    {
        fs.RegisterRootEntry(root);
        await foreach (var _ in fs.ListAsync(root.Key, includeHidden: true, ct))
        {
        }
    }

    private static async Task<Tree> BuildTreeAsync(string content, byte[]? rawBytes = null, CancellationToken ct = default)
    {
        var fs = new DelegatingFileSystemService(new LocalFileSystemService(), new StorageItemRegistry());
        var root = new FakeEntryFolder("授权夹", "content://tree/root", null);
        var file = root.AddFile("notes.md", content);
        if (rawBytes is not null)
            file.Content = rawBytes;
        await RegisterTreeAsync(fs, root, ct);
        return new Tree(fs, root, file);
    }

    [Fact]
    public async Task ReadsStorageTextWithEncodingBomAndNewLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var withBom = new List<byte>(new byte[] { 0xEF, 0xBB, 0xBF });
        withBom.AddRange(Encoding.UTF8.GetBytes("a\r\nb\r\n"));
        var tree = await BuildTreeAsync("ignored", withBom.ToArray(), ct);
        var service = new DelegatingTextFileService(new LocalTextFileService(), new StorageTextFileService(tree.Fs));

        var snapshot = await service.ReadAsync(tree.File.Key, 2 * 1024 * 1024, ct);

        Assert.Equal("a\r\nb\r\n", snapshot.Text);
        Assert.True(snapshot.HasBom);
        Assert.Equal("\r\n", snapshot.NewLine);
        Assert.Equal(TextFileEncodingKind.Utf8, snapshot.Encoding);
        // 存储提供者不暴露只读位：进入工作台后可以编辑，写回仍受指纹复核约束
        Assert.False(snapshot.IsReadOnly);
        Assert.Equal(tree.File.Content.Length, snapshot.Length);
    }

    [Fact]
    public async Task SavesInPlacePreservingBomAndNewLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var withBom = new List<byte>(new byte[] { 0xEF, 0xBB, 0xBF });
        withBom.AddRange(Encoding.UTF8.GetBytes("one\r\ntwo\r\n"));
        var tree = await BuildTreeAsync("ignored", withBom.ToArray(), ct);
        var service = new DelegatingTextFileService(new LocalTextFileService(), new StorageTextFileService(tree.Fs));
        var snapshot = await service.ReadAsync(tree.File.Key, 2 * 1024 * 1024, ct);

        var saved = await service.SaveAsync(snapshot, "one\ntwo\nthree", overwriteChanged: false, ct);

        Assert.Equal("one\r\ntwo\r\nthree", saved.Text);
        Assert.True(saved.HasBom);
        Assert.StartsWith("\uFEFF", Encoding.UTF8.GetString(tree.File.Content));
        Assert.Equal(saved.Sha256, TextFileCodecHash(tree.File.Content));
    }

    [Fact]
    public async Task DetectsExternalChangeAndOnlyOverwritesWhenExplicit()
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = await BuildTreeAsync("original", ct: ct);
        var service = new DelegatingTextFileService(new LocalTextFileService(), new StorageTextFileService(tree.Fs));
        var snapshot = await service.ReadAsync(tree.File.Key, 2 * 1024 * 1024, ct);

        tree.File.Content = Encoding.UTF8.GetBytes("changed elsewhere");
        await Assert.ThrowsAsync<TextFileChangedException>(
            () => service.SaveAsync(snapshot, "mine", overwriteChanged: false, ct));
        Assert.Equal("changed elsewhere", Encoding.UTF8.GetString(tree.File.Content));

        var reloaded = await service.ReadAsync(tree.File.Key, 2 * 1024 * 1024, ct);
        var saved = await service.SaveAsync(reloaded, "mine", overwriteChanged: true, ct);
        Assert.Equal("mine", saved.Text);
        Assert.Equal("mine", Encoding.UTF8.GetString(tree.File.Content));
    }

    [Fact]
    public async Task RejectsUnverifiableWriteAndReportsHonestly()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new DelegatingFileSystemService(new LocalFileSystemService(), new StorageItemRegistry());
        var root = new FakeEntryFolder("授权夹", "content://tree/broken", null);
        var file = new NonCommittingFile("broken.txt", "content://tree/broken/broken.txt", root, "before");
        root.Children.Add(file);
        await RegisterTreeAsync(fs, root, ct);
        var service = new DelegatingTextFileService(new LocalTextFileService(), new StorageTextFileService(fs));

        var snapshot = await service.ReadAsync(file.Key, 2 * 1024 * 1024, ct);
        var error = await Assert.ThrowsAsync<IOException>(
            () => service.SaveAsync(snapshot, "after", overwriteChanged: false, ct));

        // 提供者没有真正提交内容：必须报「无法通过复核」，不能返回成功快照
        Assert.Contains("复核", error.Message);
        Assert.Equal("before", file.ReadBack());
    }

    [Fact]
    public async Task RejectsOversizeTextBeforeWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = await BuildTreeAsync("small", ct: ct);
        var service = new DelegatingTextFileService(new LocalTextFileService(), new StorageTextFileService(tree.Fs));
        var snapshot = await service.ReadAsync(tree.File.Key, 64, ct);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.SaveAsync(snapshot, new string('x', 200), overwriteChanged: false, ct));
        Assert.Equal("small", Encoding.UTF8.GetString(tree.File.Content));
    }

    private static string TextFileCodecHash(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>提供者「接受写入但不提交」的替身：用来验证写后复核与恢复失败的诚实报错。</summary>
    private sealed class NonCommittingFile(string name, string key, FakeEntryFolder parent, string content)
        : IStorageEntryFile
    {
        private string _content = content;

        public string Name { get; } = name;
        public string Key { get; } = key;
        public bool IsFolder => false;
        public string ReadBack() => _content;

        public Task<(long? Length, DateTimeOffset LastWrite)> GetInfoAsync() =>
            Task.FromResult<(long?, DateTimeOffset)>((Encoding.UTF8.GetByteCount(_content), DateTimeOffset.UnixEpoch));

        public Task DeleteAsync() => Task.CompletedTask;

        public Task<IStorageEntry> MoveToAsync(IStorageEntryFolder destinationFolder) =>
            Task.FromResult<IStorageEntry>(this);

        public Task<Stream> OpenReadAsync() =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(_content), writable: false));

        public Task<Stream> OpenWriteAsync() => Task.FromResult<Stream>(new DiscardingStream());
    }

    private sealed class DiscardingStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            // 故意丢弃写入内容，模拟提供者未提交
            base.Dispose(disposing);
        }
    }
}
