using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.40：缩略图磁盘缓存的三件事——命中/失效、负缓存、配额。
/// 这三条决定了「翻回旧目录还卡不卡」与「坏图会不会每次重试」，所以用真实文件系统断言。
/// </summary>
public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "eidoria-thumbs-" + Guid.NewGuid().ToString("N"));

    private FileThumbnailCache Cache(long quota = FileThumbnailCache.DefaultQuotaBytes) =>
        new(_directory, quota);

    private static ThumbnailRequest Request(
        string path = "/media/a.jpg",
        long length = 4096,
        int mtimeSeconds = 1_000,
        ThumbnailTier tier = ThumbnailTier.Medium) =>
        new(path, length, DateTimeOffset.FromUnixTimeSeconds(mtimeSeconds), tier);

    private static byte[] Payload(byte seed, int size = 64) =>
        Enumerable.Repeat(seed, size).ToArray();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task StoredThumbnailComesBackByteForByte()
    {
        var cache = Cache();
        var bytes = Payload(7);

        await cache.StoreAsync(Request(), bytes, CancellationToken.None);
        var lookup = await cache.LookupAsync(Request(), CancellationToken.None);

        Assert.Equal(ThumbnailCacheStatus.Hit, lookup.Status);
        Assert.Equal(bytes, lookup.Payload);
    }

    [Fact]
    public async Task LookupMissesWhenNothingWasStored()
    {
        var lookup = await Cache().LookupAsync(Request(), CancellationToken.None);
        Assert.Equal(ThumbnailCacheStatus.Miss, lookup.Status);
        Assert.Null(lookup.Payload);
    }

    [Fact]
    public async Task ChangingTheFileInvalidatesTheEntry()
    {
        var cache = Cache();
        await cache.StoreAsync(Request(mtimeSeconds: 1_000), Payload(1), CancellationToken.None);

        // 同一个路径，但修改时间变了 = 换了内容：必须重新解码，不能拿旧图充数
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(mtimeSeconds: 2_000), CancellationToken.None)).Status);
        // 大小变化同理
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(length: 8_192), CancellationToken.None)).Status);
        // 原键仍然命中
        Assert.Equal(ThumbnailCacheStatus.Hit,
            (await cache.LookupAsync(Request(mtimeSeconds: 1_000), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task TierIsPartOfTheCacheKey()
    {
        var cache = Cache();
        await cache.StoreAsync(Request(tier: ThumbnailTier.Small), Payload(3), CancellationToken.None);

        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(tier: ThumbnailTier.Large), CancellationToken.None)).Status);
        Assert.Equal(ThumbnailCacheStatus.Hit,
            (await cache.LookupAsync(Request(tier: ThumbnailTier.Small), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task FailureIsRememberedAcrossInstances()
    {
        var cache = Cache();
        await cache.StoreFailureAsync(Request(), CancellationToken.None);

        Assert.Equal(ThumbnailCacheStatus.KnownFailure,
            (await cache.LookupAsync(Request(), CancellationToken.None)).Status);
        // 新进程（新实例）也不该再试同一张坏图：标记落盘了
        Assert.Equal(ThumbnailCacheStatus.KnownFailure,
            (await Cache().LookupAsync(Request(), CancellationToken.None)).Status);
        // 文件变了就重新给机会
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(mtimeSeconds: 5_000), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ExpiredFailureMarkersGetAnotherChance()
    {
        var cache = Cache();
        await cache.StoreFailureAsync(Request(), CancellationToken.None);

        // 把标记改成 30 小时前：失败可能是暂时的（文件被占用/网络盘没挂上），
        // 过期后必须重新给这个文件一次机会。
        foreach (var marker in Directory.EnumerateFiles(_directory, "*.fail", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddHours(-30));

        // 新实例才会重新读磁盘上的标记时间；同一实例的内存记录也带过期时间
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await Cache().LookupAsync(Request(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task StoringAThumbnailOverridesAnEarlierFailure()
    {
        var cache = Cache();
        await cache.StoreFailureAsync(Request(), CancellationToken.None);
        await cache.StoreAsync(Request(), Payload(9), CancellationToken.None);

        var lookup = await cache.LookupAsync(Request(), CancellationToken.None);
        Assert.Equal(ThumbnailCacheStatus.Hit, lookup.Status);
        var stats = await cache.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.Entries);
        Assert.Equal(0, stats.Failures); // 失败标记要一并删掉，否则两套状态自相矛盾
    }

    [Fact]
    public async Task QuotaSweepDropsOldestEntriesButKeepsTheNewest()
    {
        var cache = Cache(quota: 300);
        var first = Request(path: "/media/1.jpg");
        var second = Request(path: "/media/2.jpg");
        var third = Request(path: "/media/3.jpg");

        await cache.StoreAsync(first, Payload(1, 200), CancellationToken.None);
        await Task.Delay(20); // 让最后写入时间有先后，清扫按它排序
        await cache.StoreAsync(second, Payload(2, 200), CancellationToken.None);
        await Task.Delay(20);
        await cache.StoreAsync(third, Payload(3, 200), CancellationToken.None);

        Assert.Equal(ThumbnailCacheStatus.Hit,
            (await cache.LookupAsync(third, CancellationToken.None)).Status);
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(first, CancellationToken.None)).Status);
        var stats = await cache.GetStatsAsync(CancellationToken.None);
        Assert.True(stats.Bytes <= 300, $"清扫后应回到配额内，实际 {stats.Bytes}");
    }

    [Fact]
    public async Task ClearRemovesImagesAndFailureMarkers()
    {
        var cache = Cache();
        await cache.StoreAsync(Request(path: "/media/a.jpg"), Payload(1), CancellationToken.None);
        await cache.StoreFailureAsync(Request(path: "/media/b.jpg"), CancellationToken.None);

        var freed = await cache.ClearAsync(CancellationToken.None);

        Assert.True(freed > 0);
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(path: "/media/a.jpg"), CancellationToken.None)).Status);
        // 内存里的负缓存也要清，否则界面上“清空”了却还是不出图
        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(path: "/media/b.jpg"), CancellationToken.None)).Status);
        var stats = await cache.GetStatsAsync(CancellationToken.None);
        Assert.Equal(0, stats.Entries);
        Assert.Equal(0, stats.Failures);
    }

    [Fact]
    public async Task StatsCountFailuresSeparatelyFromImages()
    {
        var cache = Cache();
        await cache.StoreAsync(Request(path: "/media/a.jpg"), Payload(1), CancellationToken.None);
        await cache.StoreFailureAsync(Request(path: "/media/b.jpg"), CancellationToken.None);

        var stats = await cache.GetStatsAsync(CancellationToken.None);

        Assert.Equal(1, stats.Entries);
        Assert.Equal(1, stats.Failures);
        Assert.True(stats.Bytes > 0);
    }

    [Fact]
    public async Task EmptyPayloadIsNotStoredAndBlankPathsAreIgnored()
    {
        var cache = Cache();
        await cache.StoreAsync(Request(), [], CancellationToken.None);
        await cache.StoreAsync(new ThumbnailRequest(string.Empty, 1, DateTimeOffset.UnixEpoch, ThumbnailTier.Medium),
            Payload(1), CancellationToken.None);

        Assert.Equal(ThumbnailCacheStatus.Miss,
            (await cache.LookupAsync(Request(), CancellationToken.None)).Status);
        var stats = await cache.GetStatsAsync(CancellationToken.None);
        Assert.Equal(0, stats.Entries);
    }

    [Theory]
    [InlineData("Small", ThumbnailTier.Small)]
    [InlineData("medium", ThumbnailTier.Medium)]
    [InlineData("LARGE", ThumbnailTier.Large)]
    [InlineData("nonsense", ThumbnailTier.Medium)]
    [InlineData(null, ThumbnailTier.Medium)]
    public void TierNamesParseNormalizesToAMediumDefault(string? raw, ThumbnailTier expected) =>
        Assert.Equal(expected, ThumbnailTierNames.Parse(raw));

    [Fact]
    public void TierPixelsMatchTheDeclaredWidths()
    {
        Assert.Equal(128, ThumbnailTierNames.Pixels(ThumbnailTier.Small));
        Assert.Equal(192, ThumbnailTierNames.Pixels(ThumbnailTier.Medium));
        Assert.Equal(288, ThumbnailTierNames.Pixels(ThumbnailTier.Large));
        Assert.Equal("Medium", ThumbnailTierNames.ToSettingValue(ThumbnailTierNames.Parse(null)));
    }

    [Fact]
    public async Task SettingsFileWithAnUnknownTierFallsBackToMedium()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eidoria-thumb-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings.json"),
                "{\"ThumbnailTier\":\"gigantic\"}");
            var store = await SettingsStore.LoadAsync(directory, CancellationToken.None);
            Assert.Equal("Medium", store.Settings.ThumbnailTier);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
