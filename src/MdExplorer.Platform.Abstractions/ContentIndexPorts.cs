using MdExplorer.Domain;

namespace MdExplorer.Platform.Abstractions;

/// <summary>
/// 内容索引（全文）的状态。它只报事实：建没建、多少篇、占多大、什么时候更新的。
/// 「是否覆盖当前根」由调用方拿 <see cref="Roots"/> 与设置比对，不在这里猜。
/// </summary>
public sealed record ContentIndexStats(
    int DocumentCount,
    long IndexBytes,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Roots)
{
    public bool IsBuilt => DocumentCount > 0;
}

/// <summary>
/// 一条全文命中。<see cref="Snippet"/> 是从原文里截出来的上下文窗口，
/// 带省略号，不含索引内部的分词形态（那东西人看不懂，也不该出现在界面上）。
/// </summary>
/// <param name="MatchOffset">命中词在 <paramref name="Snippet"/> 里的起点；-1 表示没有可高亮区间。</param>
/// <param name="MatchLength">命中词长度；0 表示没有可高亮区间。</param>
public sealed record ContentSearchHit(
    string Path,
    string Name,
    string Snippet,
    DateTimeOffset LastWriteTime,
    long Length,
    int MatchOffset = -1,
    int MatchLength = 0);

/// <summary>
/// 内容（全文）索引端口。默认关闭的增强能力：只索引用户显式加入的本地根，
/// 只收白名单扩展名的文本（单文件 ≤ 512 KB）与 Office/OpenDocument 文档正文（v0.47，≤ 32 MB），
/// 索引数据只存本机应用数据目录，原文不落库——只存被分词的正文副本用于摘要。
/// <para>
/// 契约：<see cref="RebuildAsync"/> 是可取消的全量校准（未变的文件跳过、消失的文件删除），
/// 写库按批提交（v0.49）：**取消保留已完成的分批**，只丢当前这一批，下次重建补齐；
/// 任何情况下都不得破坏已提交的索引。索引根没变时不再重写根配置。
/// <see cref="SearchAsync"/> 只读，允许与重建并发。
/// </para>
/// </summary>
public interface IContentIndexService
{
    Task<ContentIndexStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>按给定根做一次全量校准（增量跳过未变文件）。</summary>
    Task<ContentIndexStats> RebuildAsync(
        IReadOnlyList<string> roots, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// 按全文匹配查询；查询为空或索引未建时返回空列表。
    /// <paramref name="filter"/>（v0.54，大小 / 修改时间）在 SQL 里参与筛选，所以
    /// <paramref name="maxResults"/> 截断的永远是「通过筛选之后」的结果。
    /// </summary>
    Task<IReadOnlyList<ContentSearchHit>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken, SearchFilter? filter = null);

    /// <summary>删除全部索引数据（不动用户文件，不动文件名索引）。</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 内容索引的增量维护（v0.56）。由目录监听驱动：把变化的路径交进来，只重解这几条，
/// 不再为了一个新文件把整棵树重新枚举一遍。
/// <para>
/// 契约：<see cref="ApplyChangesAsync"/> 只处理**已经在索引根内、且本来会被收录**的路径；
/// 文件消失/变得不合格就把对应的行删掉。索引没建过时它是空操作（不会凭空建库）。
/// 目录监听看不住（缓冲区溢出、平台不支持）时抛 <see cref="WatchFailed"/>，由上层提示重建——
/// 增量一旦不可信，就必须让用户知道，而不是继续给一份悄悄变旧的索引。
/// </para>
/// </summary>
public interface IContentIndexMaintenance
{
    /// <summary>按给定根启动目录监听（重复调用同根是空操作）。</summary>
    void StartWatching(IReadOnlyList<string> roots);

    /// <summary>停止监听（关闭内容索引、清除索引、退出前调用）。</summary>
    void StopWatching();

    /// <summary>应用一批变化路径，返回实际写入（新增/更新/删除）的文档数。</summary>
    Task<int> ApplyChangesAsync(IReadOnlyList<string> changedPaths, CancellationToken cancellationToken);

    /// <summary>后台增量改动完成；参数是本次写入的文档数。</summary>
    event EventHandler<int>? Changed;

    /// <summary>监听失效，增量不再可信（需要全量重建）。</summary>
    event EventHandler? WatchFailed;
}
