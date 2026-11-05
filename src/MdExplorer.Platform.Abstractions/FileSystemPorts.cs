using System.IO;
using MdExplorer.Domain;

namespace MdExplorer.Platform.Abstractions;

/// <summary>文件夹枚举与文件读写端口。定位串可为本地路径或存储 URI（FileLocator.IsStorageUri 分流，委托实现见 UI 层）。</summary>
public interface IFileSystemService
{
    IAsyncEnumerable<FileItem> ListAsync(string folderPath, bool includeHidden, CancellationToken cancellationToken);
    Task<FileItem?> StatAsync(string path, CancellationToken cancellationToken);
    Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken);
    Task<string> CreateFileAsync(string parentPath, string name, CancellationToken cancellationToken);
    Task<string> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken);
    bool Exists(string path);

    /// <summary>打开文件读流（本地=FileStream；存储形态=提供者流，后者 Length 可能不可用，调用方勿依赖）。</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 打开覆盖写入流（本地=FileStream；存储形态=提供者写入流）。
    /// 存储形态没有原子替换，调用方必须自行复核写入结果；不支持写入时抛 NotSupportedException。
    /// </summary>
    Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken);

    /// <summary>父文件夹定位串；已在根时为 null（存储形态以注册表父链/提供者为准）。</summary>
    string? GetParentPath(string path);

    /// <summary>面包屑段链（根→当前）。</summary>
    IReadOnlyList<PathSegment> GetBreadcrumbs(string path);
}

/// <summary>复制/移动/删除执行器：批处理、进度、冲突策略、部分失败明细（DEVELOPMENT.md §5.5 的 v0.1 串行版）。</summary>
public interface IFileOperationService
{
    Task<OperationOutcome> CopyAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    Task<OperationOutcome> MoveAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    Task<OperationOutcome> DeletePermanentlyAsync(
        IReadOnlyList<string> sources,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>文件夹统计结果（无权访问的子树被跳过时 IsPartial=true，数值为下限；不穿目录符号链接）。</summary>
public sealed record FolderSizeResult(long TotalBytes, long FileCount, long FolderCount, bool IsPartial);

/// <summary>文件检视端口：文件夹大小统计与流式校验和（大文件不整载内存）。</summary>
public interface IFileInspectorService
{
    Task<FolderSizeResult> GetFolderSizeAsync(string path, CancellationToken cancellationToken);

    /// <param name="algorithm">"SHA256" | "MD5"（不区分大小写）。</param>
    /// <param name="progress">0..1 完成度。</param>
    /// <returns>小写十六进制摘要。</returns>
    Task<string> ComputeHashAsync(string path, string algorithm, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>存储洞察扫描边界。所有上限都会在服务层钳制，达到硬顶时结果显式标记截断。</summary>
public sealed record StorageAnalysisOptions(
    bool IncludeHidden = false,
    int LargestFileCount = 20,
    int TopFolderCount = 12,
    int MaxEntries = 250_000,
    int MaxDuplicateCandidateFiles = 10_000);

public sealed record StorageKindUsage(FileKind Kind, long TotalBytes, long FileCount);

/// <summary>分析根的一级占用桶；IsRootFiles=true 表示根目录直属文件聚合。</summary>
public sealed record StorageFolderUsage(
    string Path,
    string Name,
    long TotalBytes,
    long FileCount,
    long FolderCount,
    bool IsRootFiles);

public sealed record StorageAnalysisResult(
    string RootPath,
    long TotalBytes,
    long FileCount,
    long FolderCount,
    bool IsPartial,
    bool WasTruncated,
    IReadOnlyList<StorageKindUsage> KindUsages,
    IReadOnlyList<StorageFolderUsage> TopFolders,
    IReadOnlyList<FileItem> LargestFiles);

public sealed record DuplicateFileGroup(long FileSize, string Sha256, IReadOnlyList<FileItem> Files)
{
    public int Count => Files.Count;
    public long TotalBytes => FileSize * Count;
    public long ReclaimableBytes => FileSize * Math.Max(0, Count - 1);
}

public sealed record StorageDuplicateResult(
    string RootPath,
    IReadOnlyList<DuplicateFileGroup> Groups,
    long ReclaimableBytes,
    int CandidateFileCount,
    int FilesHashed,
    int FilesSkipped,
    bool IsPartial,
    bool WasTruncated);

/// <summary>
/// 只读存储洞察：递归聚合容量、类型、一级子文件夹与大文件，并以同尺寸预筛 + SHA-256
/// 查找精确重复文件。此端口不提供删除/清理动作；复核后的处置仍回文件上下文。
/// </summary>
public interface IStorageInsightsService
{
    Task<StorageAnalysisResult> AnalyzeAsync(
        string rootPath,
        StorageAnalysisOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<StorageDuplicateResult> FindDuplicatesAsync(
        string rootPath,
        StorageAnalysisOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 当前文件夹外部变更监视端口：Watch 返回的句柄 Dispose 即停。变更回调已做防抖合并，
/// 且可能在任意线程触发（UI 层需自行切回 UI 线程）。监视不可用（特殊文件系统）时静默降级为不回调。
/// </summary>
public interface IFolderWatchService
{
    IDisposable Watch(string folderPath, Action changed);
}

/// <summary>回收站端口。各平台能力不一致，调用前查能力位。</summary>
public interface ITrashService
{
    bool CanMoveToTrash { get; }
    bool CanBrowseTrash { get; }
    Task MoveToTrashAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrashedItem>> ListAsync(CancellationToken cancellationToken);
    Task<TrashStats> GetStatsAsync(CancellationToken cancellationToken);
    Task RestoreAsync(string trashItemId, CancellationToken cancellationToken);
    Task DeletePermanentlyAsync(string trashItemId, CancellationToken cancellationToken);

    /// <summary>永久删除当前可浏览回收站中的全部条目；不支持浏览时实现应明确失败。</summary>
    Task EmptyAsync(CancellationToken cancellationToken);
}

public sealed record TrashedItem(
    string Id,
    string OriginalName,
    string OriginalPath,
    DateTimeOffset DeletedAt,
    bool IsFolder,
    long? SizeBytes = null);

/// <summary>回收站占用快照。QuotaBytes/RetentionDays 为 null 表示系统回收站不归应用配额管理。</summary>
public sealed record TrashStats(long UsedBytes, int ItemCount, long? QuotaBytes = null, int? RetentionDays = null)
{
    public double UsedPercent => QuotaBytes is > 0
        ? Math.Clamp(UsedBytes * 100.0 / QuotaBytes.Value, 0, 100)
        : 0;
}

/// <summary>存储卷发现。</summary>
public interface IVolumeService
{
    Task<IReadOnlyList<StorageVolume>> GetVolumesAsync(CancellationToken cancellationToken);
}

public enum KnownFolder { Home, Documents, Downloads, Pictures, Music, Videos, Desktop }

/// <summary>已知目录位置（平台语义差异在此收敛，UI 永不自行拼路径）。</summary>
public interface IKnownFoldersService
{
    string? Get(KnownFolder folder);
}

/// <summary>用系统默认方式打开文件 / 在系统管理器中显示（实现位于 UI 层，走 Avalonia Launcher）。</summary>
public interface IFileLauncherService
{
    Task<bool> OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>在系统文件管理器中显示并尽量选中目标；平台不支持“选中”时退化为打开父文件夹。</summary>
    Task<bool> RevealAsync(string path, CancellationToken cancellationToken);
}

/// <summary>系统剪贴板中的文件与纯文本（路径文本）。文件格式仅承载本地路径；存储提供者 URI 无跨应用文件语义。</summary>
public interface ISystemClipboardService
{
    Task SetFilesAsync(IReadOnlyList<string> localPaths, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> TryGetFilesAsync(CancellationToken cancellationToken);
    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

/// <summary>ZIP 压缩包端口（v0.1 以 System.IO.Compression 实现，格式扩展见选型表）。</summary>
public interface IArchiveService
{
    bool CanOpen(string path);
    Task<IReadOnlyList<ArchiveEntryItem>> ListEntriesAsync(string archivePath, int maxEntries, CancellationToken cancellationToken);
    Task<OperationOutcome> ExtractAsync(string archivePath, string destinationFolder,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// 解压全部或所选条目。options 的条目集合为 null 表示全部；条目名以 / 或 \ 结尾时，
    /// 表示该虚拟文件夹的全部后代。所有落点都必须先做 Zip Slip 校验。
    /// </summary>
    Task<OperationOutcome> ExtractEntriesAsync(string archivePath, string destinationFolder,
        ArchiveExtractOptions options, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>有界读取单个文件条目；不存在、为目录或超过上限时返回 null，禁止无界读入内存。</summary>
    Task<byte[]?> ReadEntryBytesAsync(string archivePath, string entryFullName, int maxBytes, CancellationToken cancellationToken);

    Task<OperationOutcome> CompressToZipAsync(IReadOnlyList<string> sources, string destinationZipPath,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// 压缩包解压选项：是否保留虚拟目录结构，以及同名冲突策略。默认值故意不是覆盖，
/// 避免复刻早期整包解压的静默覆盖语义；UI 必须在执行前把当前策略展示给用户。
/// </summary>
public sealed record ArchiveExtractOptions(
    IReadOnlySet<string>? EntryFullNames = null,
    bool PreserveStructure = true,
    ConflictPolicy ConflictPolicy = ConflictPolicy.KeepBoth);

public sealed record ArchiveEntryItem(string FullName, long Length, long CompressedLength, DateTimeOffset LastWriteTime, bool IsFolder)
{
    /// <summary>统一为 / 分隔的虚拟路径；目录保留尾随 /（若原条目有）。</summary>
    public string VirtualPath => FullName.Replace('\\', '/');

    public string Name
    {
        get
        {
            var path = VirtualPath.TrimEnd('/');
            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }
    }

    /// <summary>父虚拟目录；根层为空字符串，非根层以 / 结尾。</summary>
    public string ParentPath
    {
        get
        {
            var path = VirtualPath.TrimEnd('/');
            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path[..(slash + 1)] : string.Empty;
        }
    }

    public FileKind Kind => IsFolder
        ? FileKind.Folder
        : FileKindClassifier.Classify(Name);

    /// <summary>压缩后体积占原体积的比例（0~1）；空文件或目录为 0。</summary>
    public double CompressionRatio => Length > 0 ? Math.Clamp(CompressedLength / (double)Length, 0, 1) : 0;
}

/// <summary>递归文件名搜索（流式产出）。</summary>
public interface ISearchService
{
    IAsyncEnumerable<FileItem> SearchByNameAsync(SearchSpec spec, CancellationToken cancellationToken);
}

/// <summary>本地文件名索引的健康快照（根数取当前设置，条目数取已落盘快照）。</summary>
public sealed record FileNameIndexStats(
    int RootCount,
    int EntryCount,
    long IndexBytes,
    DateTimeOffset? UpdatedAt,
    bool IsCurrent,
    bool IsPartial);

/// <summary>
/// 本地文件名索引（开发文档 §6.4）：只索引用户显式配置的本地文件夹，数据只保存在应用数据目录。
/// RootPath 为空的 SearchSpec 表示跨全部索引根查询；查询命中在返回前复核真实文件，失效条目不外泄。
/// </summary>
public interface IFileNameIndexService
{
    Task<FileNameIndexStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>按当前设置中的索引根全量重建。取消时旧快照保持不变。</summary>
    Task<FileNameIndexStats> RebuildAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>删除索引快照；索引根配置保留。</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>指定文件夹是否被一份与当前根配置一致的索引快照覆盖。</summary>
    Task<bool> CoversAsync(string rootPath, CancellationToken cancellationToken);

    IAsyncEnumerable<FileItem> SearchAsync(SearchSpec spec, CancellationToken cancellationToken);
}

/// <summary>端侧语义搜索索引状态。向量仅保存在应用数据目录，不包含原始文件文本。</summary>
public sealed record SemanticSearchStats(
    int IndexedItemCount,
    long IndexBytes,
    DateTimeOffset? UpdatedAt,
    bool IsCurrent,
    bool IsPartial,
    bool IsEnabled,
    IReadOnlyList<string> ModelNames);

/// <summary>语义检索命中；Score 为归一化向量的 cosine similarity。</summary>
public sealed record SemanticSearchHit(FileItem Item, float Score, string ModelName);

/// <summary>完全离线的文本语义索引。仅在用户启用本地 AI、装有文本模型并显式建索引后运行。</summary>
public interface ISemanticSearchService
{
    Task<SemanticSearchStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>从现有文件名索引覆盖的本地根重建；取消/失败时保留上一份完整向量快照。</summary>
    Task<SemanticSearchStats> RebuildAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string query, IReadOnlySet<FileKind>? kinds, int maxResults, CancellationToken cancellationToken);

    /// <summary>删除本地语义向量；不会触碰用户文件或文本文件名索引。</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>单个文件夹被记住的呈现方式（与全局默认 ViewMode/Sort* 分开保存）。</summary>
public sealed class FolderViewPreferences
{
    public string ViewMode { get; set; } = "List";          // List | Grid | Details
    public string SortField { get; set; } = "Name";
    public string SortDirection { get; set; } = "Ascending";
}

/// <summary>应用设置模型（JSON 持久化，源生成序列化）。</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string ThemeMode { get; set; } = "System";       // System | Light | Dark
    public string SeedColor { get; set; } = "#006A6A";      // MD3 HCT 种子色
    public string ViewMode { get; set; } = "List";          // List | Grid | Details
    public string SortField { get; set; } = "Name";
    public string SortDirection { get; set; } = "Ascending";
    public bool ShowHidden { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public bool ShowPreviewPane { get; set; } = true;
    public bool ReduceMotion { get; set; }                 // 应用内低动效：关闭 Eidoria 自有位移动画与微交互位移
    public bool RememberFolderView { get; set; }           // 按文件夹记忆视图/排序；关闭时 ViewMode/Sort* 为全局当前值
    public Dictionary<string, FolderViewPreferences> FolderViewStates { get; set; } = [];
    public double DrawerPaneWidth { get; set; } = 288;    // Expanded+ 永久抽屉宽度（MaterialGridSplitter 拖后回写）
    public double PreviewPaneWidth { get; set; } = 316;   // Expanded+ 预览卡宽度（同上）
    public bool DualPaneEnabled { get; set; }             // XL 双窗格偏好；窄于 XL 时仅保留 B 会话而不显示
    public string? DualPanePath { get; set; }             // 对照窗格 B 上次所在文件夹（本地路径或 storage URI）
    public string? DualPaneViewMode { get; set; }         // B 独立视图（list/grid/details）；null 表示跟随全局默认
    public string? DualPaneSortField { get; set; }        // B 独立排序字段；null 表示跟随全局默认
    public string? DualPaneSortDirection { get; set; }    // B 独立排序方向；null 表示跟随全局默认
    public double DualPaneWidth { get; set; } = 560;      // 对照窗格 B 宽度（MaterialGridSplitter 拖后回写）
    public bool ConfirmBeforeTrash { get; set; } = true;    // 移入回收站前确认；永久删除始终确认
    public bool WatchFolderChanges { get; set; } = true;   // 外部增删改时自动刷新当前文件夹
    public int ItemPageSize { get; set; } = 120;            // 文件区每页物化的行数（60–480），不改变底层枚举
    public bool AutoLoadMore { get; set; } = true;          // 滚动距底 320 DIP 时自动续页
    public bool EnableThumbnails { get; set; } = true;      // 解码图片缩略图；关闭后列表只留类型图标
    public string Density { get; set; } = "Comfortable";    // Comfortable | Compact（列表/网格行密度）
    public bool RestoreTabsOnLaunch { get; set; } = true;   // 启动恢复上次会话标签页
    public int RecentItemLimit { get; set; } = 12;          // 最近访问保留条数（6–60）
    public double PullRefreshTriggerDistance { get; set; } = 72; // 下拉刷新触发距离（48–120 DIP）
    public bool EnablePullRefresh { get; set; } = true;      // 下拉刷新手势总开关；桌面刷新按钮与 F5/Ctrl+R 始终可用
    public double AutoLoadMoreDistance { get; set; } = 320;  // 距底多少 DIP 触发自动续页（120–600）
    public int SearchResultLimit { get; set; } = 2000;      // 递归与索引搜索的结果上限（200–20000）
    public long AppTrashQuotaBytes { get; set; } = 1_000_000_000; // Android 应用内回收站配额（200 MB–10 GB）；桌面走系统回收站
    public int AppTrashRetentionDays { get; set; } = 30;    // Android 应用内回收站保留期（1–365 天）
    public bool NotifyOnTaskCompletion { get; set; }       // 长任务完成/失败时发送操作系统通知（默认关，需用户显式开启）
    public bool PlayVideoInline { get; set; }             // 视频默认在应用内嵌面板出画面（默认关：逐帧抓取可能花屏/不同步）
    public bool MediaAutoPlay { get; set; }               // 选中媒体文件后自动开始播放（默认关）
    public bool ShowMediaDiagnostics { get; set; } = true; // 预览卡里显示媒体后端名称与诊断文本
    public bool EnableLocalAi { get; set; }              // 端侧 AI 总开关（默认关；模型需用户显式下载，未安装时不假装可用）
    public List<string> OpenTabPaths { get; set; } = [];   // 上次会话的浏览器标签页（按显示顺序）
    public int ActiveTabIndex { get; set; }                // OpenTabPaths 中上次活动标签的下标
    public List<string> RecentFolderPaths { get; set; } = []; // 最近访问文件夹（最新优先，仅本地路径）
    public List<string> RecentFilePaths { get; set; } = [];   // 最近打开文件（最新优先，仅本地路径）
    public bool UseSearchIndex { get; set; } = true;          // 文件名索引总开关；关闭时递归搜索回落实时扫描
    public List<string> SearchIndexRootPaths { get; set; } = []; // 用户显式加入的本地索引根（不预置任何目录）
    public AutomationWorkflowDefinition AutomationWorkflow { get; set; } = AutomationWorkflowDefinition.CreateDefault();
}

/// <summary>设置与收藏的持久化端口。</summary>
public interface ISettingsStore
{
    AppSettings Settings { get; }
    IReadOnlyList<string> Favorites { get; }
    Task SaveSettingsAsync(CancellationToken cancellationToken);
    Task SetFavoriteAsync(string path, bool favorite, CancellationToken cancellationToken);
    bool IsFavorite(string path);
}

/// <summary>平台能力位（UI 据此显隐入口，不猜系统）。</summary>
public sealed record PlatformCapabilities(
    bool CanMoveToTrash,
    bool CanBrowseTrash,
    bool IsAndroid,
    bool IsDesktop,
    bool UsesAppTrash = false);
