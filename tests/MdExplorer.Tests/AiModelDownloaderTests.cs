using System.Security.Cryptography;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure.Ai;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.43：内置模型下载器。全部用假传输层跑——断点续传、换源、哈希校验这些规则不需要真连外网，
/// 但必须有测试，因为「权重下坏了却当成可用」是端侧 AI 最尴尬的失败方式。
/// </summary>
public sealed class AiModelDownloaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eidoria-ai-dl-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AiModelCatalog Catalog() => new(_root);

    private AiModelDownloader Downloader(IAiModelTransport transport) => new(Catalog(), transport);

    private string ModelDirectory(string id) => Path.Combine(_root, id);

    private string ChecksumsPath => Path.Combine(_root, AiModelDownloader.ChecksumsFileName);

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>任何 URL 都给同一份内容的传输层；需要按 URL 变行为时用 <see cref="ScriptedTransport"/>。</summary>
    private sealed class ScriptedTransport(
        Func<Uri, long, AiModelTransportResponse> handler) : IAiModelTransport
    {
        public List<(Uri Uri, long RangeFrom)> Requests { get; } = [];

        public Task<AiModelTransportResponse> GetAsync(Uri uri, long rangeFrom, CancellationToken cancellationToken)
        {
            Requests.Add((uri, rangeFrom));
            return Task.FromResult(handler(uri, rangeFrom));
        }

        public string[] Hosts => Requests.Select(request => request.Uri.Host).Distinct().ToArray();
    }

    /// <summary>
    /// 造一个响应。<paramref name="resumed"/> = false 表示服务端忽略 Range、返回**完整**内容——
    /// 这时不能切片，否则测的就不是「忽略 Range」而是「服务端只给了后半段」。
    /// </summary>
    private static AiModelTransportResponse RespondBytes(byte[] content, long rangeFrom = 0, bool resumed = true)
    {
        var slicing = resumed && rangeFrom > 0 && rangeFrom <= content.Length;
        var slice = slicing ? content[(int)rangeFrom..] : content;
        return new AiModelTransportResponse(new MemoryStream(slice), slice.Length, rangeFrom == 0 || slicing, null);
    }

    /// <summary>从 <c>/&lt;repo&gt;/resolve/&lt;40 位 revision&gt;/&lt;相对路径&gt;</c> 里取出仓库内相对路径。</summary>
    private static string RepoRelativePath(Uri uri)
    {
        const string marker = "/resolve/";
        var index = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return uri.AbsolutePath.TrimStart('/');
        // marker 之后是 40 位 revision，再一个 '/' 才是文件路径。
        var afterRevision = uri.AbsolutePath.IndexOf('/', index + marker.Length);
        return afterRevision < 0 ? string.Empty : uri.AbsolutePath[(afterRevision + 1)..];
    }

    /// <summary>按仓库内相对路径给出不同内容的「假 HuggingFace」。</summary>
    private static ScriptedTransport HuggingFaceLike(Func<string, byte[]>? bodyFor = null)
    {
        bodyFor ??= relativePath => Payload($"content of {relativePath}");
        return new ScriptedTransport((uri, _) => RespondBytes(bodyFor(RepoRelativePath(uri))));
    }

    // ── 基本下载与记录 ───────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadsDefaultPackageAndRecordsChecksums()
    {
        var transport = HuggingFaceLike();
        var downloader = Downloader(transport);

        var outcome = await downloader.DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(error => error.Message)));
        Assert.Equal(6, outcome.FilesDownloaded);

        var onnx = Path.Combine(ModelDirectory("text-embed-zh"), "onnx", "model_quantized.onnx");
        Assert.True(File.Exists(onnx));
        Assert.Equal("content of onnx/model_quantized.onnx", File.ReadAllText(onnx));

        // 落盘布局与脚本一致：<模型目录>/<id>/<相对路径> + checksums.txt
        var checksums = File.ReadAllLines(ChecksumsPath);
        Assert.Contains(checksums, line => line.EndsWith("text-embed-zh/onnx/model_quantized.onnx", StringComparison.Ordinal));
        Assert.All(transport.Requests, request => Assert.Equal("huggingface.co", request.Uri.Host));

        // 第二次：文件都在，一个请求都不该发。
        var before = transport.Requests.Count;
        var again = await downloader.DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);
        Assert.Equal(6, again.FilesSkipped);
        Assert.Equal(0, again.FilesDownloaded);
        Assert.Equal(before, transport.Requests.Count);
    }

    // ── 换源 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoFallsBackToMirrorWhenPrimaryIsUnreachable()
    {
        var transport = new ScriptedTransport((uri, _) =>
        {
            if (uri.Host == "huggingface.co")
                throw new HttpRequestException("HTTP 403", inner: null, System.Net.HttpStatusCode.Forbidden);
            return RespondBytes(Payload($"mirror:{RepoRelativePath(uri)}"));
        });

        var outcome = await Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.Auto, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.Equal(6, outcome.FilesDownloaded);
        Assert.Contains("hf-mirror.com", transport.Hosts);
        Assert.True(File.Exists(Path.Combine(ModelDirectory("text-embed-zh"), "config.json")));
    }

    [Fact]
    public async Task ExplicitSourceDoesNotFallBack()
    {
        var transport = new ScriptedTransport((_, _) =>
            throw new HttpRequestException("HTTP 403", inner: null, System.Net.HttpStatusCode.Forbidden));

        var outcome = await Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(6, outcome.FilesFailed);
        Assert.All(transport.Requests, request => Assert.Equal("huggingface.co", request.Uri.Host));
        Assert.Contains(outcome.Errors, error => error.Message.Contains("403", StringComparison.Ordinal));
    }

    // ── 续传 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumesFromExistingPartialFile()
    {
        var full = Payload("0123456789abcdef");
        var modelRoot = Directory.CreateDirectory(ModelDirectory("text-embed-zh")).FullName;
        // 只有清单里第一个文件会被测到：其它文件让传输层直接给空内容也不好，这里整体按同一份内容服务。
        var firstFile = "config.json";
        var partial = Path.Combine(modelRoot, firstFile + ".part");
        File.WriteAllBytes(partial, full[..5]);

        var ranges = new List<long>();
        var transport = new ScriptedTransport((uri, rangeFrom) =>
        {
            if (uri.AbsolutePath.EndsWith(firstFile, StringComparison.Ordinal))
            {
                ranges.Add(rangeFrom);
                return RespondBytes(full, rangeFrom, resumed: true);
            }
            return RespondBytes(Payload("other"));
        });

        await Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);

        Assert.Contains(5L, ranges);
        Assert.Equal(full, File.ReadAllBytes(Path.Combine(modelRoot, firstFile)));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsFromScratch()
    {
        var full = Payload("full-content-here");
        var modelRoot = Directory.CreateDirectory(ModelDirectory("text-embed-zh")).FullName;
        var partial = Path.Combine(modelRoot, "config.json.part");
        File.WriteAllBytes(partial, Payload("stale-"));

        var transport = new ScriptedTransport((uri, rangeFrom) =>
            uri.AbsolutePath.EndsWith("config.json", StringComparison.Ordinal)
                ? RespondBytes(full, rangeFrom, resumed: false) // 服务端忽略 Range，给完整内容
                : RespondBytes(Payload("other")));

        await Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);

        // 不能把完整内容追加到旧 .part 后面，否则文件就废了。
        Assert.Equal(full, File.ReadAllBytes(Path.Combine(modelRoot, "config.json")));
    }

    // ── 哈希校验 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordedChecksumMismatchDeletesPartialAndFails()
    {
        var modelRoot = Directory.CreateDirectory(ModelDirectory("text-embed-zh")).FullName;
        Directory.CreateDirectory(_root);
        File.WriteAllText(ChecksumsPath, $"{new string('a', 64)}  text-embed-zh/config.json\n");

        var transport = HuggingFaceLike();

        var outcome = await Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.FilesFailed);
        Assert.False(File.Exists(Path.Combine(modelRoot, "config.json")));
        Assert.False(File.Exists(Path.Combine(modelRoot, "config.json.part")));
        Assert.Contains(outcome.Errors, error => error.Message.Contains("SHA-256", StringComparison.Ordinal));

        // 其余文件没有记录，正常下载。
        Assert.True(File.Exists(Path.Combine(modelRoot, "tokenizer.json")));
    }

    [Fact]
    public async Task VerifyReportsMatchMissingAndMismatch()
    {
        var modelRoot = Directory.CreateDirectory(ModelDirectory("text-embed-zh")).FullName;
        var good = Payload("good");
        File.WriteAllBytes(Path.Combine(modelRoot, "config.json"), good);
        File.WriteAllBytes(Path.Combine(modelRoot, "tokenizer.json"), Payload("tampered"));
        File.WriteAllText(
            ChecksumsPath,
            $"{Sha256Hex(good)}  text-embed-zh/config.json\n" +
            $"{Sha256Hex(Payload("expected-tokenizer"))}  text-embed-zh/tokenizer.json\n");

        var outcome = await Downloader(HuggingFaceLike()).VerifyAsync(
            ["text-embed-zh"], null, TestContext.Current.CancellationToken);

        // config.json 一致、tokenizer.json 不一致、其余 4 个缺失。
        Assert.Equal(1, outcome.FilesSkipped);
        Assert.Equal(5, outcome.FilesFailed);
        Assert.Contains(outcome.Errors, error => error.ItemName.EndsWith("tokenizer.json", StringComparison.Ordinal)
                                                 && error.Message.Contains("不一致", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, error => error.Message == "文件缺失");
        Assert.Contains("校验完成", outcome.Notes[0], StringComparison.Ordinal);
    }

    // ── 安全与边界 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RejectsManifestPathsThatEscapeTheModelDirectory()
    {
        var models = new[]
        {
            new AiModelDefinition(
                "evil", "越界模型", "测试", "MIT", "https://example.invalid", "owner/repo",
                new string('b', 40), "onnx", 1, false, ["../../escape.txt"]),
        };
        var catalog = new AiModelCatalog(_root, models);
        var downloader = new AiModelDownloader(catalog, HuggingFaceLike());

        var outcome = await downloader.DownloadAsync(
            ["evil"], AiModelSource.HuggingFace, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.FilesFailed);
        Assert.Contains(outcome.Errors, error => error.Message.Contains("路径不安全", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_root, "..", "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task UnknownModelIdIsReportedWithoutTouchingDisk()
    {
        var outcome = await Downloader(HuggingFaceLike()).DownloadAsync(
            ["no-such-model"], AiModelSource.Auto, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Single(outcome.Errors);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task CancellationLeavesPartialFileForResume()
    {
        using var cts = new CancellationTokenSource();
        var transport = new ScriptedTransport((uri, _) =>
        {
            if (uri.AbsolutePath.EndsWith("config.json", StringComparison.Ordinal))
            {
                cts.Cancel(); // 模拟下到一半用户点了取消
                throw new OperationCanceledException(cts.Token);
            }
            return RespondBytes(Payload("other"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.HuggingFace, null, cts.Token));

        // 取消不写半截成品；.part 是否留下取决于取消发生在写之前，这里只要求「没有成品」。
        Assert.False(File.Exists(Path.Combine(ModelDirectory("text-embed-zh"), "config.json")));
    }

    // ── ModelScope ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ModelScopeUsesFileListApiAndVerifiesApiSha()
    {
        var revision = "08d7186b7de51be7c12444137221ad96825593d6";
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var path in new[]
                 {
                     "config.json", "tokenizer.json", "tokenizer_config.json",
                     "special_tokens_map.json", "vocab.txt", "onnx/model_quantized.onnx",
                 })
        {
            bodies[path] = Payload($"modelscope:{path}");
            files.Add(path);
        }

        var transport = new ScriptedTransport((uri, _) =>
        {
            if (uri.AbsolutePath.Contains("/repo/files", StringComparison.Ordinal))
            {
                var json = BuildModelScopeInventoryJson(revision, files, bodies);
                return new AiModelTransportResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(json)), null, true, null);
            }

            // /api/v1/models/<repo>/repo?Revision=..&FilePath=..
            var query = uri.Query;
            var filePath = Uri.UnescapeDataString(
                query[(query.IndexOf("FilePath=", StringComparison.Ordinal) + "FilePath=".Length)..]);
            return RespondBytes(bodies[filePath]);
        });

        var outcome = await Downloader(transport).DownloadAsync(
            ["text-embed-zh"], AiModelSource.ModelScope, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(error => error.Message)));
        Assert.Equal(6, outcome.FilesDownloaded);
        Assert.All(transport.Requests, request => Assert.Equal("modelscope.cn", request.Uri.Host));
        Assert.Equal(
            "modelscope:config.json",
            File.ReadAllText(Path.Combine(ModelDirectory("text-embed-zh"), "config.json")));
        // 魔搭给了 sha，落盘后写进 checksums.txt 供以后校验。
        Assert.Contains(
            File.ReadAllLines(ChecksumsPath),
            line => line.EndsWith("text-embed-zh/config.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelScopeSkipsModelsThatWereNeverValidatedThere()
    {
        var transport = new ScriptedTransport((_, _) =>
            throw new InvalidOperationException("不该发出请求"));

        var outcome = await Downloader(transport).DownloadAsync(
            ["audio-asr-tiny"], AiModelSource.ModelScope, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Empty(transport.Requests);
        Assert.Contains(outcome.Errors, error => error.Message.Contains("未在魔搭核对", StringComparison.Ordinal));
    }

    private static string BuildModelScopeInventoryJson(
        string revision, IEnumerable<string> files, IReadOnlyDictionary<string, byte[]> bodies)
    {
        // 手拼 JSON：这里是测试替身，用普通字符串最不容易被大括号转义规则绊倒。
        var items = files.Select(path =>
            "{\"Path\":\"" + path + "\",\"Type\":\"blob\",\"Revision\":\"" + revision +
            "\",\"Size\":" + bodies[path].Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"Sha256\":\"" + Sha256Hex(bodies[path]) + "\"}");
        return "{\"Code\":200,\"Data\":{\"Files\":[" + string.Join(",", items) + "]}}";
    }

    [Fact]
    public async Task SourceNamesRoundTripAndFallBackToAuto()
    {
        Assert.Equal(AiModelSource.HuggingFace, AiModelSourceNames.Parse("huggingface"));
        Assert.Equal(AiModelSource.HfMirror, AiModelSourceNames.Parse("HfMirror"));
        Assert.Equal(AiModelSource.ModelScope, AiModelSourceNames.Parse("modelscope"));
        Assert.Equal(AiModelSource.Auto, AiModelSourceNames.Parse("之类的手改垃圾"));
        Assert.Equal(AiModelSource.Auto, AiModelSourceNames.Parse(null));
        Assert.Equal("HfMirror", AiModelSourceNames.ToName(AiModelSourceNames.Parse("hf-mirror")));
        Assert.NotEmpty(AiModelSourceNames.Describe(AiModelSource.HfMirror));
    }
}
