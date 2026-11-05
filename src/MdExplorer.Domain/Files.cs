namespace MdExplorer.Domain;

/// <summary>文件内容分类（由扩展名/类型推导，驱动图标、类别聚合与预览选择）。</summary>
public enum FileKind
{
    Folder,
    Image,
    Video,
    Audio,
    Document,   // PDF 与 Office 等文档（PDF 以扩展名单判）
    Archive,
    Code,
    Text,
    Data,       // JSON/XML/CSV 等结构化数据
    Font,
    Executable,
    Unknown,
}

/// <summary>
/// 文件定位符：当前版本（本地磁盘）内包裹绝对路径；保持值对象形态，
/// 为未来 Android SAF 文档定位预留不破坏调用方的演进点（见 DEVELOPMENT.md §5.2）。
/// </summary>
public readonly record struct FileLocator(string Path)
{
    /// <summary>存储提供者形态（Android SAF 等）下为 content:// URI；本地形态为 null。此时 Path 同为该 URI 字符串，
    /// 保证历史栈/剪贴板/收藏等以字符串为键的通道无需分叉；一切 Path.* 语义调用方须先以 IsStorageUri 分流。</summary>
    public string? StorageUri { get; init; }

    public bool IsStorage => StorageUri is not null;

    public static FileLocator FromPath(string absolutePath) => new(absolutePath);

    public static FileLocator FromStorage(string uri) => new(uri) { StorageUri = uri };

    /// <summary>字符串是否为存储提供者 URI（当前仅 Android SAF 的 content: 方案）。</summary>
    public static bool IsStorageUri(string? path) =>
        path is not null && path.StartsWith("content:", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Path;
}

/// <summary>面包屑段（服务层统一产出口径：本地由路径拆分、存储形态由注册表父链生成）。</summary>
public sealed record PathSegment(string Name, string FullPath);

/// <summary>文件/文件夹条目（不可变快照）。</summary>
public sealed record FileItem(
    FileLocator Locator,
    string Name,
    FileKind Kind,
    long? Length,
    DateTimeOffset LastWriteTime,
    bool IsHidden,
    bool IsSymlink,
    string? Extension)
{
    public string FullPath => Locator.Path;
    public bool IsFolder => Kind == FileKind.Folder;
}

/// <summary>存储卷。</summary>
public sealed record StorageVolume(
    string Id,
    string DisplayName,
    FileLocator Root,
    long? TotalBytes,
    long? FreeBytes,
    bool IsRemovable)
{
    public long? UsedBytes => TotalBytes is { } t && FreeBytes is { } f ? t - f : null;
    public double? UsedFraction => TotalBytes is > 0 && UsedBytes is { } u ? (double)u / TotalBytes.Value : null;
}

public enum SortField { Name, LastWriteTime, Size, Kind }
public enum SortDirection { Ascending, Descending }

/// <summary>递归文件名搜索描述（应用层编排见 SearchService）。</summary>
public sealed record SearchSpec(
    string Query,
    string RootPath,
    IReadOnlySet<FileKind>? Kinds = null,
    bool IncludeHidden = false,
    int MaxResults = 5000);

/// <summary>写操作类型与冲突策略（任务队列语义见 DEVELOPMENT.md §5.5，v0.1 为串行执行器）。</summary>
public enum ConflictPolicy { Overwrite, Skip, KeepBoth }

/// <summary>单项操作失败。ItemPath 在服务能确定来源定位串时填写，供任务详情精确重试失败项。</summary>
public sealed record OperationError(string ItemName, string Message, string? ItemPath = null);

public sealed record OperationOutcome(
    int SucceededCount,
    IReadOnlyList<OperationError> Errors,
    IReadOnlyList<string> ProducedPaths,
    bool Cancelled = false,
    int SkippedCount = 0)
{
    public bool Success => Errors.Count == 0 && !Cancelled;
}

public sealed record OperationProgress(
    double Fraction,
    string CurrentItemName,
    int ItemsDone,
    int ItemsTotal);
