using System.Security.Cryptography;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>
/// Local semantic index for explicitly configured roots. It stores file metadata, a fingerprint of the embedded text,
/// and quantized vectors; eligible text files contribute a bounded UTF-8/Unicode prefix, but raw contents are never persisted.
/// </summary>
public sealed class SemanticSearchService : ISemanticSearchService
{
    private const string Magic = "EIDORIA-AISEARCH";
    private const int FormatVersion = 2;
    private const int MaxDocuments = 20_000;
    private const int MaxTextChars = 4_000;
    private const long MaxIndexBytes = 96L * 1024 * 1024;
    private const float MinimumScore = 0.16f;
    private const string IndexFileName = "semantic-search-v1.bin";

    private static readonly IReadOnlySet<FileKind> AllKinds = Enum.GetValues<FileKind>().ToHashSet();
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".cs", ".fs", ".vb", ".xaml", ".axaml", ".js", ".ts", ".tsx",
        ".jsx", ".py", ".sh", ".ps1", ".html", ".htm", ".css", ".sql", ".java", ".kt", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".properties", ".gradle", ".gitignore", ".editorconfig",
    };

    private readonly string _indexPath;
    private readonly IFileNameIndexService _fileNameIndex;
    private readonly ISettingsStore _settingsStore;
    private readonly ITextEmbeddingService _embeddings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SemanticSnapshot? _snapshot;
    private bool _loaded;

    public SemanticSearchService(
        string dataDirectory,
        IFileNameIndexService fileNameIndex,
        ISettingsStore settingsStore,
        ITextEmbeddingService embeddings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _fileNameIndex = fileNameIndex ?? throw new ArgumentNullException(nameof(fileNameIndex));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        Directory.CreateDirectory(dataDirectory);
        _indexPath = Path.Combine(Path.GetFullPath(dataDirectory), IndexFileName);
    }

    public async Task<SemanticSearchStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var source = await _fileNameIndex.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        return BuildStats(snapshot, source);
    }

    public async Task<SemanticSearchStats> RebuildAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_settingsStore.Settings.EnableLocalAi)
                throw new InvalidOperationException("请先在设置中启用本地 AI。");
            if (!_settingsStore.Settings.UseSearchIndex)
                throw new InvalidOperationException("请先启用文件名索引。");

            var previousSnapshot = LoadSnapshotUnderGate();
            var sourceStats = await _fileNameIndex.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            if (!sourceStats.IsCurrent || sourceStats.UpdatedAt is null)
                throw new InvalidOperationException("请先建立或重建文件名索引，再创建 AI 语义索引。");

            var models = _embeddings.GetAvailableModels();
            if (models.Count == 0)
                throw new InvalidOperationException("没有可用的文本嵌入模型。请按设置页提示下载 BGE small zh 或 MiniLM 模型。");
            if (models.Count > 2)
                throw new InvalidDataException("语义搜索最多支持两个文本嵌入模型。");

            var items = new List<FileItem>(Math.Min(sourceStats.EntryCount, MaxDocuments + 1));
            await foreach (var item in _fileNameIndex.SearchAsync(
                               new SearchSpec(string.Empty, string.Empty, AllKinds, IncludeHidden: false,
                                   MaxResults: MaxDocuments + 1), cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsSymlink)
                    continue;
                items.Add(item);
                if (items.Count > MaxDocuments)
                    break;
            }

            var roots = ConfiguredRoots();
            var partial = sourceStats.IsPartial || items.Count > MaxDocuments;
            if (items.Count > MaxDocuments)
                items.RemoveRange(MaxDocuments, items.Count - MaxDocuments);

            var built = await Task.Run(
                    () => BuildSnapshot(items, roots, models, sourceStats.UpdatedAt.Value, partial, previousSnapshot,
                        progress, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteSnapshotAsync(built, cancellationToken).ConfigureAwait(false);
            _snapshot = built;
            _loaded = true;
            var stats = await _fileNameIndex.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            return BuildStats(built, stats);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string query,
        IReadOnlySet<FileKind>? kinds,
        int maxResults,
        CancellationToken cancellationToken)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return [];
        if (!_settingsStore.Settings.EnableLocalAi)
            throw new InvalidOperationException("请先在设置中启用本地 AI。");

        var snapshot = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var sourceStats = await _fileNameIndex.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !BuildStats(snapshot, sourceStats).IsCurrent)
            throw new InvalidOperationException("AI 语义索引尚未建立或已经过期，请先重建索引。");

        var currentModels = _embeddings.GetAvailableModels();
        var model = SelectModel(query, snapshot.Models, currentModels);
        if (model is null)
            throw new InvalidOperationException("AI 索引所用模型当前不可用；请检查模型目录并重建 AI 索引。");

        var queryVector = await Task.Run(
                () => _embeddings.Embed(model.Id, query, isQuery: true, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        ValidateEmbedding(queryVector, model.Dimensions, model.Name, "查询");

        var limit = Math.Clamp(maxResults, 1, 5000);
        var modelIndex = snapshot.Models.FindIndex(candidate => candidate.Id == model.Id);
        if (modelIndex < 0)
            throw new InvalidDataException($"AI 索引中缺少模型 {model.Id} 的向量。");
        var best = new PriorityQueue<SemanticCandidate, float>();
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kinds is { Count: > 0 } && !kinds.Contains(entry.Kind))
                continue;
            if (modelIndex >= entry.Vectors.Length)
                continue;

            var score = Cosine(queryVector, entry.Vectors[modelIndex]);
            if (score < MinimumScore)
                continue;
            var candidate = new SemanticCandidate(entry, score);
            if (best.Count < limit)
                best.Enqueue(candidate, score);
            else if (best.TryPeek(out _, out var lowest) && score > lowest)
            {
                best.Dequeue();
                best.Enqueue(candidate, score);
            }
        }

        var results = new List<SemanticSearchHit>(best.Count);
        foreach (var result in best.UnorderedItems.OrderByDescending(item => item.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileItem? current;
            try
            {
                current = LocalFileSystemService.StatPath(result.Element.Entry.Path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (current is null || current.IsSymlink || current.IsFolder != result.Element.Entry.IsFolder ||
                (current.Length ?? 0) != result.Element.Entry.Length ||
                current.LastWriteTime.UtcDateTime.Ticks != result.Element.Entry.LastWriteUtcTicks)
                continue;
            results.Add(new SemanticSearchHit(current, result.Element.Score, model.Name));
        }
        return results;
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_indexPath))
                File.Delete(_indexPath);
            _snapshot = null;
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SemanticSnapshot BuildSnapshot(
        IReadOnlyList<FileItem> items,
        IReadOnlyList<string> roots,
        IReadOnlyList<TextEmbeddingModel> models,
        DateTimeOffset sourceIndexBuiltAt,
        bool partial,
        SemanticSnapshot? previousSnapshot,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = new List<SemanticEntry>(items.Count);
        var total = Math.Max(1, items.Count * models.Count);
        var done = 0;
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var previousModelIndexes = MapReusableModels(previousSnapshot?.Models, models);
        var previousEntries = new Dictionary<string, SemanticEntry>(pathComparer);
        if (previousSnapshot is not null && previousModelIndexes is not null)
        {
            foreach (var previousEntry in previousSnapshot.Entries)
            {
                if (previousEntry.Vectors.Length == previousSnapshot.Models.Count)
                    previousEntries.TryAdd(previousEntry.Path, previousEntry);
            }
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ComposeIndexText(item, cancellationToken);
            var contentFingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            byte[][] vectors;
            if (previousModelIndexes is not null && previousEntries.TryGetValue(item.FullPath, out var oldEntry) &&
                MetadataMatches(oldEntry, item) &&
                oldEntry.ContentFingerprint.AsSpan().SequenceEqual(contentFingerprint))
            {
                // Reuse persisted vectors only when both metadata and the exact bounded text fed to the
                // embedder match. This also catches same-size edits whose timestamp was restored.
                vectors = new byte[models.Count][];
                for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
                    vectors[modelIndex] = oldEntry.Vectors[previousModelIndexes[modelIndex]];
                done += models.Count;
            }
            else
            {
                vectors = new byte[models.Count][];
                for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var embedding = _embeddings.Embed(models[modelIndex].Id, text, isQuery: false, cancellationToken);
                    ValidateEmbedding(embedding, models[modelIndex].Dimensions, models[modelIndex].Name, "文件");
                    vectors[modelIndex] = Quantize(embedding);
                    done++;
                }
            }

            if (done == total || done % 16 == 0)
                progress?.Report(new OperationProgress((double)done / total, item.Name, done, total));

            // Ignore a file that changed while it was being embedded or reused. A fresh rebuild will pick up its new content.
            try
            {
                var current = LocalFileSystemService.StatPath(item.FullPath);
                if (current is null || current.IsSymlink || current.IsFolder != item.IsFolder ||
                    (current.Length ?? 0) != (item.Length ?? 0) ||
                    current.LastWriteTime.UtcDateTime.Ticks != item.LastWriteTime.UtcDateTime.Ticks)
                    continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            entries.Add(new SemanticEntry(
                item.FullPath,
                item.Name,
                item.Kind,
                item.IsFolder,
                item.Length ?? 0,
                item.LastWriteTime.UtcDateTime.Ticks,
                contentFingerprint,
                vectors));
        }

        return new SemanticSnapshot(DateTimeOffset.UtcNow, sourceIndexBuiltAt, partial, roots, models.ToList(), entries);
    }

    private static int[]? MapReusableModels(
        IReadOnlyList<TextEmbeddingModel>? previousModels,
        IReadOnlyList<TextEmbeddingModel> currentModels)
    {
        if (previousModels is null || previousModels.Count != currentModels.Count)
            return null;

        var oldIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < previousModels.Count; i++)
        {
            if (!oldIndexes.TryAdd(previousModels[i].Id, i))
                return null;
        }

        var mapping = new int[currentModels.Count];
        for (var i = 0; i < currentModels.Count; i++)
        {
            var current = currentModels[i];
            if (!oldIndexes.TryGetValue(current.Id, out var oldIndex))
                return null;
            var previous = previousModels[oldIndex];
            if (previous.Revision != current.Revision || previous.Dimensions != current.Dimensions)
                return null;
            mapping[i] = oldIndex;
        }
        return mapping;
    }

    private static bool MetadataMatches(SemanticEntry entry, FileItem item) =>
        entry.Name == item.Name && entry.Kind == item.Kind && entry.IsFolder == item.IsFolder &&
        entry.Length == (item.Length ?? 0) && entry.LastWriteUtcTicks == item.LastWriteTime.UtcDateTime.Ticks;

    private static string ComposeIndexText(FileItem item, CancellationToken cancellationToken)
    {
        var name = item.Name;
        var parent = Path.GetFileName(Path.GetDirectoryName(item.FullPath)?.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty);
        var text = new StringBuilder(512);
        text.Append("文件名：").Append(name);
        if (!string.IsNullOrWhiteSpace(parent))
            text.Append("。所在文件夹：").Append(parent);

        if (!item.IsFolder && item.Length is <= 2 * 1024 * 1024 &&
            item.Extension is { } extension && TextExtensions.Contains(extension))
        {
            try
            {
                using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: false);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096, leaveOpen: false);
                var buffer = new char[MaxTextChars];
                var read = reader.Read(buffer, 0, buffer.Length);
                cancellationToken.ThrowIfCancellationRequested();
                if (read > 0)
                {
                    var content = new string(buffer, 0, read);
                    if (!content.Contains('\0'))
                        text.Append("。内容摘要：").Append(content);
                }
            }
            catch (DecoderFallbackException)
            {
                // A mislabelled/binary file still remains searchable by its filename only.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return text.ToString();
    }

    private SemanticSearchStats BuildStats(SemanticSnapshot? snapshot, FileNameIndexStats sourceStats)
    {
        var enabled = _settingsStore.Settings.EnableLocalAi;
        var available = _embeddings.GetAvailableModels();
        var names = available.Select(model => model.Name).ToArray();
        var bytes = 0L;
        try
        {
            bytes = new FileInfo(_indexPath) is { Exists: true } info ? info.Length : 0;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var modelSetMatches = snapshot is not null && snapshot.Models.Count == available.Count &&
            snapshot.Models.Zip(available).All(pair => pair.First.Id == pair.Second.Id && pair.First.Revision == pair.Second.Revision);
        var rootsMatch = snapshot is not null && RootsEqual(snapshot.Roots, ConfiguredRoots());
        var sourceMatches = snapshot?.SourceIndexBuiltAt == sourceStats.UpdatedAt;
        var current = snapshot is not null && enabled && _settingsStore.Settings.UseSearchIndex &&
            available.Count > 0 && sourceStats.IsCurrent && modelSetMatches && rootsMatch && sourceMatches;

        return new SemanticSearchStats(
            snapshot?.Entries.Count ?? 0,
            bytes,
            snapshot?.BuiltAt,
            current,
            snapshot?.IsPartial ?? false,
            enabled,
            names);
    }

    private async Task<SemanticSnapshot?> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return _snapshot;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return _snapshot;
            return LoadSnapshotUnderGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the previous index while the caller already owns <see cref="_gate"/>.</summary>
    private SemanticSnapshot? LoadSnapshotUnderGate()
    {
        if (_loaded)
            return _snapshot;
        try
        {
            if (File.Exists(_indexPath))
                _snapshot = ReadSnapshot(_indexPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            _snapshot = null;
        }
        _loaded = true;
        return _snapshot;
    }

    private async Task WriteSnapshotAsync(SemanticSnapshot snapshot, CancellationToken cancellationToken)
    {
        var tempPath = _indexPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(FormatVersion);
                writer.Write(snapshot.BuiltAt.UtcDateTime.Ticks);
                writer.Write(snapshot.SourceIndexBuiltAt.UtcDateTime.Ticks);
                writer.Write(snapshot.IsPartial);
                writer.Write(snapshot.Roots.Count);
                foreach (var root in snapshot.Roots)
                    WriteString(writer, root, 32 * 1024);
                writer.Write(snapshot.Models.Count);
                foreach (var model in snapshot.Models)
                {
                    WriteString(writer, model.Id, 256);
                    WriteString(writer, model.Name, 2048);
                    WriteString(writer, model.Revision, 128);
                    writer.Write(model.Dimensions);
                }
                writer.Write(snapshot.Entries.Count);
                foreach (var entry in snapshot.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteString(writer, entry.Path, 32 * 1024);
                    WriteString(writer, entry.Name, 16 * 1024);
                    writer.Write((int)entry.Kind);
                    writer.Write(entry.IsFolder);
                    writer.Write(entry.Length);
                    writer.Write(entry.LastWriteUtcTicks);
                    if (entry.ContentFingerprint.Length != SHA256.HashSizeInBytes)
                        throw new InvalidDataException("AI 语义索引中的文本指纹异常。");
                    writer.Write(entry.ContentFingerprint);
                    for (var modelIndex = 0; modelIndex < snapshot.Models.Count; modelIndex++)
                    {
                        var vector = entry.Vectors[modelIndex];
                        if (vector.Length != snapshot.Models[modelIndex].Dimensions)
                            throw new InvalidDataException("向量维数不一致，无法写入 AI 索引。");
                        writer.Write(vector);
                    }
                    if (stream.Position > MaxIndexBytes)
                        throw new InvalidDataException("AI 语义索引超过本地存储上限，请减少索引范围。");
                }
                writer.Flush();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _indexPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static SemanticSnapshot ReadSnapshot(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaxIndexBytes)
            throw new InvalidDataException("AI 语义索引文件大小异常。");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Encoding.ASCII.GetByteCount(Magic));
        var formatVersion = reader.ReadInt32();
        if (!magic.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Magic)) || formatVersion is < 1 or > FormatVersion)
            throw new InvalidDataException("AI 语义索引文件版本不受支持。");
        var builtAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var sourceBuiltAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var partial = reader.ReadBoolean();
        var rootCount = ReadCount(reader, 64);
        var roots = new List<string>(rootCount);
        for (var i = 0; i < rootCount; i++)
            roots.Add(ReadString(reader, 32 * 1024));

        var modelCount = ReadCount(reader, 2);
        if (modelCount == 0)
            throw new InvalidDataException("AI 语义索引没有模型元数据。");
        var models = new List<TextEmbeddingModel>(modelCount);
        for (var i = 0; i < modelCount; i++)
        {
            var id = ReadString(reader, 256);
            var name = ReadString(reader, 2048);
            var revision = ReadString(reader, 128);
            var dimensions = reader.ReadInt32();
            if (dimensions is <= 0 or > 2048)
                throw new InvalidDataException("AI 语义索引向量维数异常。");
            models.Add(new TextEmbeddingModel(id, name, revision, dimensions));
        }

        var entryCount = ReadCount(reader, MaxDocuments);
        var entries = new List<SemanticEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var entryPath = ReadString(reader, 32 * 1024);
            var name = ReadString(reader, 16 * 1024);
            var kindValue = reader.ReadInt32();
            if (!Enum.IsDefined(typeof(FileKind), kindValue))
                throw new InvalidDataException("AI 语义索引文件类型不受支持。");
            var isFolder = reader.ReadBoolean();
            var length = reader.ReadInt64();
            var writeTicks = reader.ReadInt64();
            if (length < 0 || writeTicks < DateTime.MinValue.Ticks || writeTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("AI 语义索引文件信息异常。");
            var contentFingerprint = formatVersion >= 2
                ? reader.ReadBytes(SHA256.HashSizeInBytes)
                : Array.Empty<byte>();
            if (formatVersion >= 2 && contentFingerprint.Length != SHA256.HashSizeInBytes)
                throw new EndOfStreamException("AI 语义索引文本指纹被截断。");
            var vectors = new byte[modelCount][];
            for (var modelIndex = 0; modelIndex < modelCount; modelIndex++)
            {
                vectors[modelIndex] = reader.ReadBytes(models[modelIndex].Dimensions);
                if (vectors[modelIndex].Length != models[modelIndex].Dimensions)
                    throw new EndOfStreamException("AI 语义索引向量被截断。");
            }
            entries.Add(new SemanticEntry(entryPath, name, (FileKind)kindValue, isFolder, length, writeTicks,
                contentFingerprint, vectors));
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("AI 语义索引包含未识别的尾随数据。");
        return new SemanticSnapshot(builtAt, sourceBuiltAt, partial, roots, models, entries);
    }

    private static void WriteString(BinaryWriter writer, string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maxBytes)
            throw new InvalidDataException("AI 语义索引中的文本字段超过长度上限。");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int maxBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException("AI 语义索引中的文本长度异常。");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("AI 语义索引文本字段被截断。");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("AI 语义索引的条目数异常。");
        return count;
    }

    private static void ValidateEmbedding(float[] embedding, int expectedDimensions, string modelName, string inputName)
    {
        if (embedding.Length != expectedDimensions)
            throw new InvalidDataException($"{inputName}嵌入维数与模型 {modelName} 不一致。");

        var norm = 0d;
        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException($"模型 {modelName} 为{inputName}生成了非有限值。");
            norm += (double)value * value;
        }
        if (!double.IsFinite(norm) || norm <= 1e-20)
            throw new InvalidDataException($"模型 {modelName} 为{inputName}生成了零向量或无效向量。");
    }

    private static byte[] Quantize(float[] embedding)
    {
        var vector = new byte[embedding.Length];
        for (var i = 0; i < embedding.Length; i++)
            vector[i] = unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(embedding[i] * 127f), -127, 127));
        return vector;
    }

    private static float Cosine(float[] query, byte[] indexed)
    {
        if (query.Length != indexed.Length)
            return -1;
        var dot = 0d;
        for (var i = 0; i < query.Length; i++)
            dot += query[i] * unchecked((sbyte)indexed[i]) / 127d;
        return (float)Math.Clamp(dot, -1d, 1d);
    }

    private TextEmbeddingModel? SelectModel(
        string query,
        IReadOnlyList<TextEmbeddingModel> indexed,
        IReadOnlyList<TextEmbeddingModel> available)
    {
        var byId = available.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var matches = indexed.Where(model => byId.TryGetValue(model.Id, out var current) && current.Revision == model.Revision).ToArray();
        if (matches.Length == 0)
            return null;
        var preferredId = ContainsCjk(query) ? "text-embed-zh" : "text-embed-en";
        return matches.FirstOrDefault(model => model.Id == preferredId) ?? matches[0];
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x3040 and <= 0x30FF)
                return true;
        }
        return false;
    }

    private IReadOnlyList<string> ConfiguredRoots()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer);
        foreach (var path in _settingsStore.Settings.SearchIndexRootPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || FileLocator.IsStorageUri(path) || !Path.IsPathFullyQualified(path))
                continue;
            try
            {
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
        }
        return roots.OrderBy(path => path, comparer).ToArray();
    }

    private static bool RootsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return left.Zip(right).All(pair => comparison.Equals(pair.First, pair.Second));
    }

    private sealed record SemanticSnapshot(
        DateTimeOffset BuiltAt,
        DateTimeOffset SourceIndexBuiltAt,
        bool IsPartial,
        IReadOnlyList<string> Roots,
        List<TextEmbeddingModel> Models,
        List<SemanticEntry> Entries);

    private sealed record SemanticEntry(
        string Path,
        string Name,
        FileKind Kind,
        bool IsFolder,
        long Length,
        long LastWriteUtcTicks,
        byte[] ContentFingerprint,
        byte[][] Vectors);

    private sealed record SemanticCandidate(SemanticEntry Entry, float Score);
}
