using System.Globalization;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using Microsoft.Data.Sqlite;

namespace MdExplorer.Infrastructure;

/// <summary>
/// SQLite FTS5 内容索引（默认关闭的增强能力）。
/// <para>
/// 分词策略：中文没有空格，FTS5 的 <c>unicode61</c> 会把一整段汉字当成一个超长 token，
/// 于是「搜任意片段」根本匹配不上。这里在写入索引前把每段连续汉字展开成**重叠二元组**
/// （"文件管理器" → "文件 件管 管理 理器"），查询侧做同样的展开再用 AND 连接，
/// 于是两个汉字的查询（最常见的形态）也能命中。拉丁字母/数字保持原样，交给 unicode61。
/// </para>
/// <para>
/// 为什么不用 <c>trigram</c> 分词器：它要求查询至少 3 个字符，而中文最短常用查询是 2 个字。
/// 代价是二元组会带来少量假阳性，排序用 bm25 —— 可接受，也比「2 个字查不到」强。
/// </para>
/// <para>
/// 摘要不交给 <c>snippet()</c>：索引里存的是二元组展开后的形态，直接取出来是「文件 件管 …」。
/// 这里保留一份原文列，命中后在原文里找第一个出现位置并截窗口。
/// </para>
/// </summary>
public sealed class SqliteContentIndex : IContentIndexService
{
    /// <summary>单文件正文上限（与文档口径一致）；超过的文件不进索引。</summary>
    public const long MaxFileBytes = 512 * 1024;

    /// <summary>Office / OpenDocument 文档的体积上限（v0.47）：压缩包里有大量版式，不能按纯文本口径卡 512 KB。</summary>
    public const long MaxDocumentBytes = 32L * 1024 * 1024;

    /// <summary>只索引这些扩展名的正文；其余类型连打开都不打开。</summary>
    public static readonly IReadOnlyList<string> IndexedExtensions =
    [
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".sql", ".html", ".htm",
        ".css", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".py", ".cs", ".java", ".kt",
        ".go", ".rs", ".c", ".h", ".cpp", ".hpp", ".sh", ".bash", ".ps1", ".bat", ".cmd", ".srt", ".tex",
    ];

    private const int SnippetWindow = 60;

    private readonly string _databasePath;
    private readonly ITextFileService _textFiles;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _initGate = new();
    private bool _initialized;
    private string? _initError;

    public SqliteContentIndex(string databasePath, ITextFileService textFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _textFiles = textFiles ?? throw new ArgumentNullException(nameof(textFiles));
    }

    public string DatabasePath => _databasePath;

    // ── 统计 ─────────────────────────────────────────────────────────────────

    public async Task<ContentIndexStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        // 只是看状态时不该顺手建库：功能没开就不该在磁盘上留下一个空索引。
        if (!_initialized && !File.Exists(_databasePath))
            return new ContentIndexStats(0, 0, null, []);

        if (!await TryEnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return new ContentIndexStats(0, MeasureBytes(), null, []);

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), MAX(updated_at) FROM docs;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new ContentIndexStats(0, MeasureBytes(), null, []);
            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            DateTimeOffset? updatedAt = reader.IsDBNull(1)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
            await reader.DisposeAsync().ConfigureAwait(false);

            var roots = await ReadRootsAsync(connection, cancellationToken).ConfigureAwait(false);
            return new ContentIndexStats(count, MeasureBytes(), updatedAt, roots);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return new ContentIndexStats(0, MeasureBytes(), null, []);
        }
    }

    // ── 重建（全量校准） ──────────────────────────────────────────────────────

    public async Task<ContentIndexStats> RebuildAsync(
        IReadOnlyList<string> roots, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalizedRoots = NormalizeRoots(roots);
        if (!await TryEnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return await GetStatsAsync(cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            // 整次重建放在一个事务里：中途取消或抛错就整体回滚，已有索引保持原样。
            await using var transaction = connection.BeginTransaction();

            // 现有索引：path -> (id, mtime, size)。未变的文件直接跳过，避免整库重解。
            var existing = new Dictionary<string, (long Id, long Mtime, long Size)>(StringComparer.Ordinal);
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT id, path, mtime, size FROM docs;";
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    existing[reader.GetString(1)] = (reader.GetInt64(0), reader.GetInt64(2), reader.GetInt64(3));
            }

            var candidates = EnumerateCandidates(normalizedRoots, cancellationToken);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var done = 0;
            var total = candidates.Count;

            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                seen.Add(file.FullName);
                done++;
                if (done % 25 == 0 || done == total)
                {
                    progress?.Report(new OperationProgress(
                        total == 0 ? 0 : (double)done / total, file.Name, done, total));
                }

                long mtime;
                long length;
                try
                {
                    // 存 unix 毫秒而不是 Ticks：查询侧要按毫秒还原成 DateTimeOffset，
                    // 两边必须是同一个单位，否则命中结果的时间会离谱。
                    mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                    length = file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // 枚举后文件被删/改名：跳过，下一轮再校准
                }

                if (existing.TryGetValue(file.FullName, out var known) &&
                    known.Mtime == mtime && known.Size == length)
                {
                    continue; // 没变：跳过解码
                }

                string? text;
                try
                {
                    // v0.47：Office / ODF 走正文抽取（ZIP + XML 流式扫文本）。
                    text = OfficeDocumentText.IsSupportedExtension(Path.GetExtension(file.Name))
                        ? OfficeDocumentText.TryExtract(file.FullName)
                        : (await _textFiles.ReadAsync(file.FullName, MaxFileBytes, cancellationToken)
                            .ConfigureAwait(false)).Text;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    // 读不了的文件（超过上限、无权限、编码不认识）不进索引，也不让整次重建失败。
                    text = null;
                }

                if (text is null)
                {
                    if (existing.TryGetValue(file.FullName, out var stale))
                        await DeleteDocumentAsync(connection, transaction, stale.Id, cancellationToken)
                            .ConfigureAwait(false);
                    continue;
                }

                await UpsertDocumentAsync(
                    connection, transaction, file, text, mtime, length,
                    existing.TryGetValue(file.FullName, out var current) ? current.Id : null,
                    cancellationToken).ConfigureAwait(false);
            }

            // 消失在根里的文档：删掉，避免「文件早删了还能搜到」。
            foreach (var (path, row) in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Contains(path))
                    await DeleteDocumentAsync(connection, transaction, row.Id, cancellationToken).ConfigureAwait(false);
            }

            await WriteRootsAsync(connection, transaction, normalizedRoots, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }

        return await GetStatsAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── 查询 ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ContentSearchHit>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var matchQuery = BuildMatchQuery(query);
        if (matchQuery.Length == 0 || maxResults <= 0)
            return [];
        if (!await TryEnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return [];

        var hits = new List<ContentSearchHit>();
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.path, d.name, d.raw, d.mtime, d.size
                FROM docs_fts f
                JOIN docs d ON d.id = f.rowid
                WHERE docs_fts MATCH $match
                ORDER BY bm25(docs_fts)
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$match", matchQuery);
            command.Parameters.AddWithValue("$limit", maxResults);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var raw = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var snippet = BuildSnippet(raw, query, out var matchOffset, out var matchLength);
                hits.Add(new ContentSearchHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    snippet,
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.GetInt64(4),
                    matchOffset,
                    matchLength));
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // 查询语法无法识别（例如纯符号）等：返回空，不炸界面。
            return [];
        }

        return hits;
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using (var wipe = connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = "DELETE FROM docs_fts; DELETE FROM docs; DELETE FROM meta;";
                await wipe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            // 让 -wal 也一起收掉：界面显示的体积要跟用户看到的一致。
            try
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                await using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // 压缩失败只影响体积显示，不影响「已清空」这个事实。
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ── 分词与摘要（internal：单元测试直接验证这两条规则） ────────────────────

    /// <summary>
    /// 把正文展开成「可被 FTS5 匹配」的形态：连续汉字 → 重叠二元组，其余字符原样保留。
    /// </summary>
    internal static string ExpandForIndex(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length + text.Length / 2);
        var runStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsCjk(text[i]))
            {
                runStart = runStart < 0 ? i : runStart;
                continue;
            }

            if (runStart >= 0)
            {
                AppendCjkRun(builder, text.AsSpan(runStart, i - runStart));
                runStart = -1;
            }
            builder.Append(text[i]);
        }

        if (runStart >= 0)
            AppendCjkRun(builder, text.AsSpan(runStart, text.Length - runStart));
        return builder.ToString();
    }

    /// <summary>
    /// 把查询串变成 FTS5 的 MATCH 表达式：每个词加引号（避免用户输入被当成语法），
    /// 词之间用 AND。纯空白/纯符号返回空串，调用方据此不再查库。
    /// </summary>
    internal static string BuildMatchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var tokens = TokenizeQuery(query);
        if (tokens.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            if (builder.Length > 0)
                builder.Append(" AND ");
            builder.Append('"').Append(token.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        return builder.ToString();
    }

    private static List<string> TokenizeQuery(string query)
    {
        var tokens = new List<string>();
        var runStart = -1;
        var word = new StringBuilder();
        void FlushWord()
        {
            if (word.Length == 0)
                return;
            tokens.Add(word.ToString().ToLowerInvariant());
            word.Clear();
        }

        for (var i = 0; i < query.Length; i++)
        {
            var ch = query[i];
            if (IsCjk(ch))
            {
                FlushWord();
                runStart = runStart < 0 ? i : runStart;
                continue;
            }

            if (runStart >= 0)
            {
                AppendCjkRunTokens(tokens, query.AsSpan(runStart, i - runStart));
                runStart = -1;
            }

            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
            {
                word.Append(ch);
            }
            else
            {
                FlushWord();
            }
        }

        if (runStart >= 0)
            AppendCjkRunTokens(tokens, query.AsSpan(runStart, query.Length - runStart));
        FlushWord();
        return tokens;
    }

    private static void AppendCjkRunTokens(List<string> tokens, ReadOnlySpan<char> run)
    {
        if (run.Length == 1)
        {
            tokens.Add(run[0].ToString());
            return;
        }
        for (var i = 0; i + 1 < run.Length; i++)
            tokens.Add(run.Slice(i, 2).ToString());
    }

    private static void AppendCjkRun(StringBuilder builder, ReadOnlySpan<char> run)
    {
        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
            builder.Append(' ');
        if (run.Length == 1)
        {
            builder.Append(run[0]);
            builder.Append(' ');
            return;
        }
        for (var i = 0; i + 1 < run.Length; i++)
        {
            builder.Append(run[i]);
            builder.Append(run[i + 1]);
            builder.Append(' ');
        }
    }

    /// <summary>从原文里截一段带上下文的窗口；找不到原查询时退回开头。</summary>
    internal static string BuildSnippet(string raw, string query) =>
        BuildSnippet(raw, query, out _, out _);

    /// <summary>
    /// 同上，另外给出命中词在返回摘要里的偏移量（v0.48 高亮用）。
    /// 偏移量按**摘要字符串**计，不含前缀省略号之外的任何假设——
    /// UI 直接照着切三段，不需要自己再搜一遍关键词。
    /// </summary>
    internal static string BuildSnippet(string raw, string query, out int matchOffset, out int matchLength)
    {
        matchOffset = -1;
        matchLength = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var flat = Collapse(raw);
        var needle = query.Trim();
        var index = needle.Length == 0
            ? 0
            : flat.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        // 找不到时窗口仍从头开始，但**不能**把这个 0 当成命中位置——
        // 否则 UI 会把开头三个字当成命中词加亮（v0.48 单测抓到的就是这个）。
        var found = index;
        if (index < 0)
            index = 0;

        var start = Math.Max(0, index - SnippetWindow);
        var length = Math.Min(flat.Length - start, needle.Length + SnippetWindow * 2);
        var slice = flat.Substring(start, Math.Max(0, length));
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < flat.Length ? "…" : string.Empty;
        if (found >= 0 && needle.Length > 0 && found >= start && found + needle.Length <= start + length)
        {
            matchOffset = prefix.Length + (found - start);
            matchLength = needle.Length;
        }

        return prefix + slice + suffix;
    }

    private static string Collapse(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (!lastWasSpace && builder.Length > 0)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(ch);
            lastWasSpace = false;
        }
        return builder.ToString().Trim();
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u3400' and <= '\u4DBF'   // 扩展 A
            or >= '\u4E00' and <= '\u9FFF'  // 基本汉字
            or >= '\uF900' and <= '\uFAFF'  // 兼容汉字
            or >= '\u3040' and <= '\u30FF'; // 日文假名（同样没有空格分词）

    // ── 数据库 ───────────────────────────────────────────────────────────────

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<bool> TryEnsureSchemaAsync(CancellationToken cancellationToken)
    {
        lock (_initGate)
        {
            if (_initialized)
                return true;
            if (_initError is not null)
                return false;
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // docs：元数据 + 原文（摘要用）；docs_fts：分词后的正文，用 rowid 与 docs.id 对齐。
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS docs(
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    mtime INTEGER NOT NULL,
                    size INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    raw TEXT NOT NULL);
                CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5(tokens, tokenize='unicode61');
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            lock (_initGate)
            {
                _initialized = true;
            }
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // 例如 SQLite 构建不带 FTS5、数据目录不可写：如实记下，功能降级为「未建立」。
            lock (_initGate)
            {
                _initError = ex.Message;
            }
            return false;
        }
    }

    private static async Task UpsertDocumentAsync(
        SqliteConnection connection, SqliteTransaction transaction, FileInfo file, string text,
        long mtimeMs, long length, long? existingId, CancellationToken cancellationToken)
    {
        var tokens = ExpandForIndex(text);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (existingId is { } id)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE docs SET name=$name, mtime=$mtime, size=$size, updated_at=$now, raw=$raw WHERE id=$id;
                """;
            update.Parameters.AddWithValue("$name", file.Name);
            update.Parameters.AddWithValue("$mtime", mtimeMs);
            update.Parameters.AddWithValue("$size", length);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$raw", text);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var updateFts = connection.CreateCommand();
            updateFts.Transaction = transaction;
            updateFts.CommandText = "UPDATE docs_fts SET tokens=$tokens WHERE rowid=$id;";
            updateFts.Parameters.AddWithValue("$tokens", tokens);
            updateFts.Parameters.AddWithValue("$id", id);
            await updateFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO docs(path, name, mtime, size, updated_at, raw)
            VALUES($path, $name, $mtime, $size, $now, $raw);
            """;
        insert.Parameters.AddWithValue("$path", file.FullName);
        insert.Parameters.AddWithValue("$name", file.Name);
        insert.Parameters.AddWithValue("$mtime", mtimeMs);
        insert.Parameters.AddWithValue("$size", length);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$raw", text);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var lastId = connection.CreateCommand();
        lastId.Transaction = transaction;
        lastId.CommandText = "SELECT last_insert_rowid();";
        var inserted = await lastId.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var newId = Convert.ToInt64(inserted, CultureInfo.InvariantCulture);

        await using var insertFts = connection.CreateCommand();
        insertFts.Transaction = transaction;
        insertFts.CommandText = "INSERT INTO docs_fts(rowid, tokens) VALUES($id, $tokens);";
        insertFts.Parameters.AddWithValue("$id", newId);
        insertFts.Parameters.AddWithValue("$tokens", tokens);
        await insertFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteDocumentAsync(
        SqliteConnection connection, SqliteTransaction transaction, long id, CancellationToken cancellationToken)
    {
        await using var deleteFts = connection.CreateCommand();
        deleteFts.Transaction = transaction;
        deleteFts.CommandText = "DELETE FROM docs_fts WHERE rowid=$id;";
        deleteFts.Parameters.AddWithValue("$id", id);
        await deleteFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var deleteDoc = connection.CreateCommand();
        deleteDoc.Transaction = transaction;
        deleteDoc.CommandText = "DELETE FROM docs WHERE id=$id;";
        deleteDoc.Parameters.AddWithValue("$id", id);
        await deleteDoc.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRootsAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM meta WHERE key='roots';
            INSERT INTO meta(key, value) VALUES('roots', $roots);
            """;
        command.Parameters.AddWithValue("$roots", string.Join('\n', roots));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ReadRootsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='roots';";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(value))
            return [];
        return value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private long MeasureBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                var info = new FileInfo(_databasePath + suffix);
                if (info.Exists)
                    total += info.Length;
            }
            catch (IOException)
            {
            }
        }
        return total;
    }

    private static List<string> NormalizeRoots(IReadOnlyList<string> roots)
    {
        var normalized = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            try
            {
                var full = Path.GetFullPath(root);
                if (Directory.Exists(full) && !normalized.Contains(full, StringComparer.Ordinal))
                    normalized.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return normalized;
    }

    /// <summary>
    /// 枚举候选文件：不穿符号链接/联接点，跳过隐藏目录（<c>.git</c> 之类）与本仓的
    /// <c>bin</c>/<c>obj</c>/<c>node_modules</c>；只收白名单扩展名与不超过上限的文件。
    /// </summary>
    private static List<FileInfo> EnumerateCandidates(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var results = new List<FileInfo>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (!visited.Add(directory))
                    continue;

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry is DirectoryInfo child)
                    {
                        try
                        {
                            if (ShouldSkipDirectory(child))
                                continue;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        pending.Push(child.FullName);
                        continue;
                    }

                    if (entry is not FileInfo file)
                        continue;
                    var extension = Path.GetExtension(file.Name);
                    var isDocument = OfficeDocumentText.IsSupportedExtension(extension);
                    if (!isDocument &&
                        !IndexedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var limit = isDocument ? MaxDocumentBytes : MaxFileBytes;
                        if (file.Length <= 0 || file.Length > limit)
                            continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    results.Add(file);
                }
            }
        }
        return results;
    }

    private static bool ShouldSkipDirectory(DirectoryInfo directory) =>
        directory.Name.StartsWith('.') ||
        directory.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
