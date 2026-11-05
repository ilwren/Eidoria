using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MdExplorer.Domain;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>模型权重的下载源。用户可在设置里显式指定，或让应用按顺序自动回落。</summary>
public enum AiModelSource
{
    /// <summary>按 HuggingFace → hf-mirror → ModelScope 的顺序自动回落。</summary>
    Auto,
    HuggingFace,
    HfMirror,
    ModelScope,
}

/// <summary>下载源的字符串形态（存进设置 JSON 的就是这些名字）；非法值一律收回 Auto。</summary>
public static class AiModelSourceNames
{
    public const string Auto = nameof(AiModelSource.Auto);
    public const string HuggingFace = nameof(AiModelSource.HuggingFace);
    public const string HfMirror = nameof(AiModelSource.HfMirror);
    public const string ModelScope = nameof(AiModelSource.ModelScope);

    public static AiModelSource Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "huggingface" or "hf" => AiModelSource.HuggingFace,
        "hfmirror" or "hf-mirror" or "mirror" => AiModelSource.HfMirror,
        "modelscope" or "ms" => AiModelSource.ModelScope,
        _ => AiModelSource.Auto,
    };

    public static string ToName(AiModelSource source) => source switch
    {
        AiModelSource.HuggingFace => HuggingFace,
        AiModelSource.HfMirror => HfMirror,
        AiModelSource.ModelScope => ModelScope,
        _ => Auto,
    };

    /// <summary>给界面看的说明：这一档实际会去哪、失败了会怎样。</summary>
    public static string Describe(AiModelSource source) => source switch
    {
        AiModelSource.HuggingFace => "只走 huggingface.co。海外网络通常最快，被拒时不会自动换源。",
        AiModelSource.HfMirror => "只走 hf-mirror.com（HuggingFace 公共镜像）。境内网络更稳，镜像滞后时可能缺文件。",
        AiModelSource.ModelScope => "只走 modelscope.cn。仅清单里已核对过魔搭仓库与 revision 的模型可下载，其余会如实报「该源未验证」。",
        _ => "按 huggingface.co → hf-mirror.com → modelscope.cn 依次尝试：前一个源失败或文件缺失时自动换下一个。",
    };
}

/// <summary>一次 HTTP 取数的结果；<see cref="Resumed"/> 为 false 表示服务端忽略了 Range，需要从头写。</summary>
public sealed class AiModelTransportResponse(Stream content, long? contentLength, bool resumed, IDisposable? owner)
    : IDisposable
{
    public Stream Content { get; } = content;
    public long? ContentLength { get; } = contentLength;

    /// <summary>true = 从请求的偏移继续；false = 服务端给了完整内容（必须丢掉已有 .part）。</summary>
    public bool Resumed { get; } = resumed;

    public void Dispose()
    {
        Content.Dispose();
        owner?.Dispose();
    }
}

/// <summary>
/// 下载用的最小传输面。抽出来是为了让下载逻辑能在没有网络的情况下被测试：
/// 断点续传、哈希不符重试、换源顺序都不需要真的连外网。
/// </summary>
public interface IAiModelTransport
{
    /// <summary>取 <paramref name="uri"/>；<paramref name="rangeFrom"/> &gt; 0 时尝试续传。</summary>
    Task<AiModelTransportResponse> GetAsync(Uri uri, long rangeFrom, CancellationToken cancellationToken);
}

/// <summary>真实实现：共享一个 <see cref="HttpClient"/>，不设整体超时（大文件），靠连接超时 + 取消令牌兜底。</summary>
public sealed class HttpAiModelTransport : IAiModelTransport
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        // 权重大则几分钟，不设整体超时；取消由 CancellationToken 负责。
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public const string UserAgent = "Eidoria/1.0 (+https://github.com/ilwren/Eidoria)";

    public async Task<AiModelTransportResponse> GetAsync(
        Uri uri, long rangeFrom, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (rangeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(rangeFrom, null);

        var response = await Shared
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"HTTP {code}", inner: null, response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new AiModelTransportResponse(
            stream,
            response.Content.Headers.ContentLength,
            rangeFrom == 0 || response.StatusCode == HttpStatusCode.PartialContent,
            response);
    }
}

/// <summary>一次下载/校验的结果。只报事实：下成了几个、跳过了几个、失败原因是什么。</summary>
public sealed record AiModelDownloadOutcome(
    bool Cancelled,
    int FilesDownloaded,
    int FilesSkipped,
    int FilesFailed,
    IReadOnlyList<OperationError> Errors,
    IReadOnlyList<string> Notes)
{
    public bool Success => !Cancelled && FilesFailed == 0;

    /// <summary>任务中心要的成功计数：下成的 + 本来就在的。</summary>
    public int SucceededCount => FilesDownloaded + FilesSkipped;
}

/// <summary>模型下载端口：内置下载器（v0.43 起不再要求用户去跑 PowerShell/bash 脚本）。</summary>
public interface IAiModelDownloader
{
    /// <summary>按模型 id 下载缺失文件；已存在且校验记录一致的文件直接跳过。</summary>
    Task<AiModelDownloadOutcome> DownloadAsync(
        IReadOnlyList<string> modelIds,
        AiModelSource source,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>只校验，不下载：用 <c>checksums.txt</c> 复核已有文件。</summary>
    Task<AiModelDownloadOutcome> VerifyAsync(
        IReadOnlyList<string> modelIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 内置模型下载器。
/// <para>
/// 与 <c>scripts/fetch-ai-models*.{sh,ps1,py}</c> 共用同一份 <c>scripts/ai-models.manifest</c>，
/// 落盘布局也一致（<c>&lt;模型目录&gt;/&lt;id&gt;/&lt;仓库内相对路径&gt;</c> + <c>checksums.txt</c>），
/// 所以脚本下过一半的目录可以直接由应用接着续。三条硬规则：
/// </para>
/// <list type="number">
/// <item>先写 <c>.part</c>，校验通过才原子改名——应用永远看不到半截文件。</item>
/// <item>能续就续（HTTP Range），服务端不支持时从头来；已记录的 SHA-256 不符就删掉重下一次。</item>
/// <item>换源只换「文件级」的失败：一个源拿到的是错误页/被墙，不影响下一个源接着试。</item>
/// </list>
/// </summary>
public sealed class AiModelDownloader : IAiModelDownloader
{
    public const string ChecksumsFileName = "checksums.txt";
    public const string HuggingFaceEndpoint = "https://huggingface.co";
    public const string HfMirrorEndpoint = "https://hf-mirror.com";
    public const string ModelScopeEndpoint = "https://modelscope.cn";
    public const string ModelscopeEndpointEnvironmentVariable = "MDEXPLORER_MODELSCOPE_ENDPOINT";
    public const string HfEndpointEnvironmentVariable = "MDEXPLORER_HF_ENDPOINT";
    public const string HfMirrorEndpointEnvironmentVariable = "MDEXPLORER_HF_MIRROR_ENDPOINT";

    /// <summary>每个文件最多尝试几次（第一次 + 一次重试）。哈希不符/网络截断都算。</summary>
    private const int MaxAttemptsPerFile = 2;

    private const int BufferBytes = 128 * 1024;

    /// <summary>
    /// ModelScope 仓库的固定 revision。与 <c>scripts/fetch-ai-models-modelscope.py</c> 的
    /// <c>MODELSCOPE_REVISIONS</c> 保持一致；清单里没有这两列，所以只对核对过的模型开放魔搭源。
    /// </summary>
    private static readonly Dictionary<string, string> ModelScopeRevisions = new(StringComparer.Ordinal)
    {
        ["text-embed-zh"] = "08d7186b7de51be7c12444137221ad96825593d6",
        ["text-embed-en"] = "e374d41ef0f72744177114e9980ea32ccc4f5ae6",
    };

    private readonly AiModelCatalog _catalog;
    private readonly IAiModelTransport _transport;

    public AiModelDownloader(AiModelCatalog catalog, IAiModelTransport? transport = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _transport = transport ?? new HttpAiModelTransport();
    }

    // ── 对外：下载 ───────────────────────────────────────────────────────────

    public async Task<AiModelDownloadOutcome> DownloadAsync(
        IReadOnlyList<string> modelIds,
        AiModelSource source,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        var models = ResolveModels(modelIds, out var unknown);
        var errors = new List<OperationError>();
        var notes = new List<string>();
        foreach (var id in unknown)
            errors.Add(new OperationError(id, "清单里没有这个模型 id"));

        if (models.Count == 0)
            return new AiModelDownloadOutcome(false, 0, 0, errors.Count, errors, notes);

        var checksums = LoadChecksums();
        var total = models.Sum(model => model.Files.Count);
        var done = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var sources = ResolveSources(source);
        // 一个模型只拉一次魔搭文件清单，6 个文件不该打 6 次 API。
        var modelScopeCache = new Dictionary<string, ModelScopeInventory>(StringComparer.Ordinal);
        // 粘性源：某个源这次成功过，后面的文件先试它——否则每个文件都要在墙外先等一次超时。
        AiModelSource? stickySource = null;

        foreach (var model in models)
        {
            var modelRoot = Path.Combine(_catalog.ModelsDirectory, model.Id);
            var perSourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            Directory.CreateDirectory(modelRoot);

            foreach (var relativePath in model.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = $"{model.Id}/{relativePath}";

                if (!TryResolveDestination(modelRoot, relativePath, out var destination, out var unsafeReason))
                {
                    failed++;
                    errors.Add(new OperationError(label, $"清单路径不安全：{unsafeReason}"));
                    done++;
                    continue;
                }

                var info = new FileInfo(destination);
                if (info.Exists && info.Length > 0)
                {
                    // 已在的文件不重复下载（除非哈希记录明确说不对）。校验是显式动作，不在这里做。
                    done++;
                    skipped++;
                    Report(progress, done, total, label, 1);
                    continue;
                }

                var expected = checksums.TryGetValue(label, out var recorded) ? recorded : null;
                var tryOrder = stickySource is { } sticky
                    ? sources.OrderBy(candidate => candidate == sticky ? 0 : 1).ToArray()
                    : sources;
                var outcome = await FetchFileAsync(
                        model, relativePath, destination, expected, tryOrder, perSourceCounts, modelScopeCache,
                        cancellationToken, (fileFraction, bytesInFile) =>
                            Report(progress, done, total, label, fileFraction))
                    .ConfigureAwait(false);

                done++;
                if (outcome.Success)
                {
                    stickySource = outcome.Source;
                    downloaded++;
                    Report(progress, done, total, label, 1);
                    continue;
                }

                failed++;
                errors.Add(new OperationError(label, outcome.Error ?? "下载失败", destination));
                Report(progress, done, total, label, 1);
            }

            if (perSourceCounts.Count > 0)
            {
                var summary = string.Join("、", perSourceCounts
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key} {pair.Value}"));
                notes.Add($"{model.Name}：下载 {summary} 个文件");
            }
        }

        return new AiModelDownloadOutcome(
            Cancelled: false,
            FilesDownloaded: downloaded,
            FilesSkipped: skipped,
            FilesFailed: failed,
            Errors: errors,
            Notes: notes);
    }

    // ── 对外：校验 ───────────────────────────────────────────────────────────

    public async Task<AiModelDownloadOutcome> VerifyAsync(
        IReadOnlyList<string> modelIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        var models = ResolveModels(modelIds, out var unknown);
        var errors = new List<OperationError>();
        var notes = new List<string>();
        foreach (var id in unknown)
            errors.Add(new OperationError(id, "清单里没有这个模型 id"));

        var checksums = LoadChecksums();
        var total = models.Sum(model => model.Files.Count);
        var done = 0;
        var ok = 0;
        var missing = 0;
        var mismatched = 0;
        var unrecorded = 0;

        foreach (var model in models)
        {
            var modelRoot = Path.Combine(_catalog.ModelsDirectory, model.Id);
            foreach (var relativePath in model.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = $"{model.Id}/{relativePath}";
                if (!TryResolveDestination(modelRoot, relativePath, out var destination, out var unsafeReason))
                {
                    mismatched++;
                    errors.Add(new OperationError(label, $"清单路径不安全：{unsafeReason}"));
                    done++;
                    continue;
                }

                Report(progress, done, total, label, 0);
                var info = new FileInfo(destination);
                if (!info.Exists || info.Length == 0)
                {
                    missing++;
                    errors.Add(new OperationError(label, "文件缺失", destination));
                }
                else if (!checksums.TryGetValue(label, out var expected))
                {
                    // 脚本先下过、或旧版本没记录：不算错，但也不能声称校验通过。
                    unrecorded++;
                    notes.Add($"{label}：没有 SHA-256 记录，无法判断是否完整");
                }
                else if (await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false) is { } actual &&
                         string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    ok++;
                }
                else
                {
                    mismatched++;
                    errors.Add(new OperationError(label, "SHA-256 与 checksums.txt 不一致", destination));
                }

                done++;
                Report(progress, done, total, label, 1);
            }
        }

        if (ok + unrecorded + missing + mismatched == 0)
            notes.Add("没有可校验的模型");

        notes.Insert(0,
            $"校验完成：一致 {ok}、无记录 {unrecorded}、缺失 {missing}、不一致 {mismatched}");
        return new AiModelDownloadOutcome(
            Cancelled: false,
            FilesDownloaded: 0,
            FilesSkipped: ok,
            FilesFailed: missing + mismatched,
            Errors: errors,
            Notes: notes);
    }

    // ── 单文件：换源 + 续传 + 哈希 ───────────────────────────────────────────

    private sealed record FileFetchOutcome(bool Success, string? Error, AiModelSource Source);

    private async Task<FileFetchOutcome> FetchFileAsync(
        AiModelDefinition model,
        string relativePath,
        string destination,
        string? expectedSha256,
        IReadOnlyList<AiModelSource> sources,
        Dictionary<string, int> perSourceCounts,
        Dictionary<string, ModelScopeInventory> modelScopeCache,
        CancellationToken cancellationToken,
        Action<double, long> onProgress)
    {
        var failures = new List<string>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Uri uri;
            string? apiSha = null;
            long? apiSize = null;
            if (source == AiModelSource.ModelScope)
            {
                if (!ModelScopeRevisions.TryGetValue(model.Id, out var revision))
                {
                    failures.Add("ModelScope：该模型未在魔搭核对过，已跳过");
                    continue;
                }

                ModelScopeInventory modelScope;
                try
                {
                    if (!modelScopeCache.TryGetValue(model.Id, out var cached))
                    {
                        cached = await LoadModelScopeInventoryAsync(model, revision, cancellationToken)
                            .ConfigureAwait(false);
                        modelScopeCache[model.Id] = cached;
                    }
                    modelScope = cached;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    failures.Add($"ModelScope：文件清单获取失败（{Describe(ex.Message)}）");
                    continue;
                }

                if (!modelScope.Files.TryGetValue(relativePath, out var entry) || entry.Revision != revision)
                {
                    failures.Add($"ModelScope：镜像里没有 {relativePath}（或 revision 不一致）");
                    continue;
                }

                apiSha = entry.Sha256;
                apiSize = entry.Size;
                uri = BuildModelScopeFileUri(model, relativePath, revision);
            }
            else
            {
                uri = BuildHuggingFaceUri(source, model, relativePath);
            }

            var effectiveExpected = apiSha ?? expectedSha256;
            for (var attempt = 1; attempt <= MaxAttemptsPerFile; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadToPartAsync(uri, destination, effectiveExpected, apiSize, cancellationToken, onProgress)
                        .ConfigureAwait(false);
                    perSourceCounts[SourceLabel(source)] =
                        perSourceCounts.TryGetValue(SourceLabel(source), out var count) ? count + 1 : 1;
                    return new FileFetchOutcome(true, null, source);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    var message = Describe(ex.Message);
                    if (attempt == MaxAttemptsPerFile)
                        failures.Add($"{SourceLabel(source)}：{message}");
                }
            }
        }

        return new FileFetchOutcome(
            false,
            failures.Count == 0 ? "没有可用的下载源" : string.Join("；", failures),
            AiModelSource.Auto);
    }

    private async Task DownloadToPartAsync(
        Uri uri,
        string destination,
        string? expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken,
        Action<double, long> onProgress)
    {
        var partial = destination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var response = await _transport.GetAsync(uri, offset, cancellationToken).ConfigureAwait(false);

        if (offset > 0 && !response.Resumed)
            offset = 0; // 服务端忽略了 Range：只能从头来

        var mode = offset > 0 ? FileMode.Append : FileMode.Create;
        var total = (response.ContentLength is { } length ? length + offset : (long?)null) ?? expectedSize;

        await using (var file = new FileStream(partial, mode, FileAccess.Write, FileShare.None, BufferBytes, useAsync: true))
        {
            var buffer = new byte[BufferBytes];
            long written = offset;
            while (true)
            {
                var read = await response.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                onProgress(total is > 0 ? Math.Min(1d, (double)written / total.Value) : 0d, written);
            }
        }

        if (expectedSize is { } size)
        {
            var actualSize = new FileInfo(partial).Length;
            if (actualSize != size)
            {
                TryDelete(partial);
                throw new InvalidDataException($"大小不符（期望 {size} 字节，实际 {actualSize} 字节）");
            }
        }

        var sha = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
        if (sha is null)
            throw new IOException("读取下载的临时文件失败");

        if (expectedSha256 is { Length: > 0 })
        {
            if (!string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partial);
                throw new InvalidDataException(
                    $"SHA-256 不符（期望 {expectedSha256[..Math.Min(12, expectedSha256.Length)]}…，" +
                    $"实际 {sha[..12]}…）");
            }
        }

        // 通过校验才改名：应用永远看不到半截文件。
        File.Move(partial, destination, overwrite: true);
        RecordChecksum(destination, sha);
    }

    /// <summary>把刚下好的文件哈希记进 <c>checksums.txt</c>（key 形如 <c>id/相对路径</c>）。</summary>
    private void RecordChecksum(string destination, string sha)
    {
        var relative = Path.GetRelativePath(_catalog.ModelsDirectory, destination)
            .Replace(Path.DirectorySeparatorChar, '/');
        var checksums = LoadChecksums();
        checksums[relative] = sha;
        SaveChecksums(checksums);
    }

    // ── ModelScope ───────────────────────────────────────────────────────────

    private sealed record ModelScopeEntry(string Revision, long Size, string? Sha256);

    private sealed record ModelScopeInventory(Dictionary<string, ModelScopeEntry> Files);

    private async Task<ModelScopeInventory> LoadModelScopeInventoryAsync(
        AiModelDefinition model, string revision, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(AiModelSource.ModelScope);
        var uri = new Uri(
            $"{endpoint}/api/v1/models/{model.Source}/repo/files?Revision={revision}&Recursive=true");
        using var response = await _transport.GetAsync(uri, 0, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(response.Content, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ok = root.TryGetProperty("Code", out var code) &&
                 code.ValueKind == JsonValueKind.Number &&
                 code.TryGetInt32(out var codeValue) && codeValue == 200;
        if (!ok)
        {
            var message = root.TryGetProperty("Message", out var m) ? m.ToString() : "未知错误";
            throw new InvalidDataException(
                $"ModelScope 文件清单接口返回 {code.ValueKind}/{code}：{message}");
        }

        var files = new Dictionary<string, ModelScopeEntry>(StringComparer.Ordinal);
        if (root.TryGetProperty("Data", out var data) &&
            data.TryGetProperty("Files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("Path", out var pathElement) ||
                    !item.TryGetProperty("Type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeElement.GetString(), "blob", StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = pathElement.GetString();
                if (string.IsNullOrEmpty(path))
                    continue;

                var entryRevision = item.TryGetProperty("Revision", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? string.Empty
                    : string.Empty;
                var size = item.TryGetProperty("Size", out var s) && s.TryGetInt64(out var parsed) ? parsed : 0;
                var sha = item.TryGetProperty("Sha256", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString()
                    : null;

                files[path] = new ModelScopeEntry(entryRevision, size, sha);
            }
        }

        return new ModelScopeInventory(files);
    }

    private Uri BuildModelScopeFileUri(AiModelDefinition model, string relativePath, string revision)
    {
        var endpoint = ResolveEndpoint(AiModelSource.ModelScope);
        var query = $"Revision={Uri.EscapeDataString(revision)}&FilePath={Uri.EscapeDataString(relativePath)}";
        return new Uri($"{endpoint}/api/v1/models/{model.Source}/repo?{query}");
    }

    private Uri BuildHuggingFaceUri(AiModelSource source, AiModelDefinition model, string relativePath)
    {
        var endpoint = ResolveEndpoint(source);
        var path = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{endpoint}/{model.Source}/resolve/{model.Revision}/{path}");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────────

    private IReadOnlyList<AiModelDefinition> ResolveModels(IReadOnlyList<string> modelIds, out List<string> unknown)
    {
        var wanted = new HashSet<string>(modelIds, StringComparer.Ordinal);
        var models = _catalog.Models.Where(model => wanted.Contains(model.Id)).ToArray();
        unknown = wanted.Except(models.Select(model => model.Id), StringComparer.Ordinal).ToList();
        return models;
    }

    private static IReadOnlyList<AiModelSource> ResolveSources(AiModelSource source) => source switch
    {
        AiModelSource.Auto => [AiModelSource.HuggingFace, AiModelSource.HfMirror, AiModelSource.ModelScope],
        _ => [source],
    };

    private static string ResolveEndpoint(AiModelSource source) => source switch
    {
        AiModelSource.HfMirror => Trim(Environment.GetEnvironmentVariable(HfMirrorEndpointEnvironmentVariable))
                                  ?? HfMirrorEndpoint,
        AiModelSource.ModelScope => Trim(Environment.GetEnvironmentVariable(ModelscopeEndpointEnvironmentVariable))
                                    ?? ModelScopeEndpoint,
        _ => Trim(Environment.GetEnvironmentVariable(HfEndpointEnvironmentVariable)) ?? HuggingFaceEndpoint,
    };

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');

    private static string SourceLabel(AiModelSource source) => source switch
    {
        AiModelSource.HuggingFace => "huggingface",
        AiModelSource.HfMirror => "hf-mirror",
        AiModelSource.ModelScope => "modelscope",
        _ => "auto",
    };

    /// <summary>清单里的路径必须是仓库内的相对路径；绝对路径或 <c>..</c> 一律拒绝（不信任数据文件）。</summary>
    private static bool TryResolveDestination(
        string modelRoot, string relativePath, out string destination, out string? reason)
    {
        destination = string.Empty;
        reason = null;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            reason = "路径为空";
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(":", StringComparison.Ordinal))
        {
            reason = "看起来是绝对路径";
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            reason = "含空段或 ..";
            return false;
        }

        var root = Path.GetFullPath(modelRoot);
        var combined = Path.GetFullPath(Path.Combine(segments.Prepend(root).ToArray()));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            reason = "越出了模型目录";
            return false;
        }

        destination = combined;
        return true;
    }

    private string ChecksumsPath => Path.Combine(_catalog.ModelsDirectory, ChecksumsFileName);

    /// <summary>读 <c>checksums.txt</c>（与脚本文本格式一致：<c>&lt;sha256&gt;  &lt;id/相对路径&gt;</c>）。</summary>
    internal Dictionary<string, string> LoadChecksums()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(ChecksumsPath))
                return result;
            foreach (var raw in File.ReadAllLines(ChecksumsPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0].Length != 64)
                    continue;
                result[parts[1].Trim()] = parts[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记录坏了只是「无法校验」，不该让下载整体失败。
        }
        return result;
    }

    internal void SaveChecksums(Dictionary<string, string> checksums)
    {
        try
        {
            Directory.CreateDirectory(_catalog.ModelsDirectory);
            var builder = new StringBuilder();
            foreach (var pair in checksums.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                builder.Append(pair.Value).Append("  ").Append(pair.Key).Append('\n');
            var temp = ChecksumsPath + ".part";
            File.WriteAllText(temp, builder.ToString(), Encoding.ASCII);
            File.Move(temp, ChecksumsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记不下来就下次当「没有记录」处理，不静默冒充校验通过。
        }
    }

    private static async Task<string?> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or NotSupportedException
            or TaskCanceledException { CancellationToken.IsCancellationRequested: false };

    private static string Describe(string message)
    {
        // 给用户看的失败原因要能定位到「网络/权限/校验」哪一类，而不是一串异常类型名。
        var text = message.Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private static void Report(
        IProgress<OperationProgress>? progress, int done, int total, string current, double fileFraction)
    {
        if (progress is null)
            return;
        var fraction = total <= 0 ? 0 : Math.Min(1d, (done + Math.Clamp(fileFraction, 0, 1)) / total);
        progress.Report(new OperationProgress(fraction, current, done, total));
    }

    /// <summary>给界面用的「这一档源会去哪」摘要。</summary>
    public static string DescribeSources(AiModelSource source) => AiModelSourceNames.Describe(source);
}
