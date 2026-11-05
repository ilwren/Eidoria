using System.Globalization;
using Avalonia;
using Md3.Avalonia.Controls;
using MdExplorer.Domain;
using MdExplorer.UI.Preview;

namespace MdExplorer.UI.ViewModels;

/// <summary>主页/抽屉中的卷条目（顶层类型便于 XAML 编译绑定引用）。</summary>
public sealed record VolumeItemModel(string Name, string Path, string UsageText, double UsedPercent, string? Glyph)
{
    public string UsedPercentText => $"{UsedPercent:0}%";
}

/// <summary>
/// 已授权文件夹（Android SAF）管理行。Path 为 content:// 定位串；失效书签保留行但 Path 为空，
/// 让用户能定点重新授权，而不是在下次启动时静默消失。
/// </summary>
public sealed record AuthorizedRootModel(
    string Name,
    string Path,
    string? Bookmark = null,
    bool IsAvailable = true,
    bool IsPersistent = true)
{
    public bool NeedsReauthorization => !IsAvailable;
    public string StateText => !IsAvailable
        ? "需要重新授权"
        : IsPersistent ? "读写 · 已保存" : "读写 · 仅本次会话";
    public string LocationText => !IsAvailable
        ? "文件夹不可用"
        : IsPersistent ? "重启后仍可用" : "重启后需重新授权";
    public string PathToolTip => IsAvailable ? Path : "文件夹不可用或授权已失效";
    public string? StatusGlyph => !IsAvailable
        ? MdSymbols.Warning
        : IsPersistent ? MdSymbols.CheckCircle : MdSymbols.PendingActions;
}

/// <summary>文件名索引根行（仅本地文件夹）。</summary>
public sealed record SearchIndexRootModel(string Name, string Path);

/// <summary>回收站行。</summary>
public sealed record TrashItemModel(
    string Id,
    string Name,
    string OriginalPath,
    string DeletedText,
    bool IsFolder,
    long? SizeBytes = null)
{
    public bool ShowOriginalPath => !FileLocator.IsStorageUri(OriginalPath);
    public bool HasDeletedText => !string.IsNullOrWhiteSpace(DeletedText);
    public string SizeText => SizeBytes is { } bytes ? Formatting.Bytes(bytes) : "大小未知";
    public string MetaText => string.IsNullOrWhiteSpace(DeletedText)
        ? SizeText
        : $"{DeletedText} · {SizeText}";
    public string? Glyph => IsFolder
        ? MdSymbols.Folder
        : FileKindClassifier.Classify(Name) switch
        {
            FileKind.Image => MdSymbols.Image,
            FileKind.Video => MdSymbols.Movie,
            FileKind.Audio => MdSymbols.MusicNote,
            FileKind.Document => Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? MdSymbols.PictureAsPdf
                : MdSymbols.Description,
            FileKind.Archive => MdSymbols.FolderZip,
            FileKind.Code => MdSymbols.Code,
            FileKind.Data => MdSymbols.DataObject,
            FileKind.Font => MdSymbols.FontDownload,
            FileKind.Executable => MdSymbols.Terminal,
            FileKind.Text => MdSymbols.TextSnippet,
            _ => MdSymbols.InsertDriveFile,
        };
}

/// <summary>预览元数据行。</summary>
public sealed record MetadataRowModel(string Key, string Value);

/// <summary>压缩包条目行。</summary>
public sealed record ArchiveEntryRowModel(string Name, string SizeText);

/// <summary>KeePass 分组行（按层级扁平成一条列表，用 Depth 缩进）。</summary>
public sealed record KeePassGroupRowModel(string Path, string Name, int EntryCount, int Depth)
{
    public Thickness IndentMargin => new(Depth * 16, 0, 0, 0);
    public string CountText => EntryCount.ToString("N0", CultureInfo.InvariantCulture) + " 个条目";
}

/// <summary>KeePass 条目行；只读展示，不含任何写回入口。</summary>
public sealed record KeePassEntryRowModel(KeePassEntryNode Entry)
{
    public string Title => Entry.Title;
    public string GroupPath => Entry.GroupPath;
    public string Subtitle => string.Join(
        " · ",
        new[] { Entry.UserName, Entry.Url }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool HasSubtitle => Subtitle.Length > 0;
}

/// <summary>KeePass 字段行；受保护字段（密码、TOTP）由 ViewModel 决定是否遮罩。</summary>
public sealed record KeePassFieldRowModel(string Key, string Value, bool IsProtected);

/// <summary>存储洞察的类型占用行。</summary>
public sealed record StorageKindUsageModel(
    FileKind Kind,
    string Name,
    string? Glyph,
    long TotalBytes,
    long FileCount,
    double Percent)
{
    public string SizeText => Formatting.Bytes(TotalBytes);
    public string PercentText => Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    public string FileCountText => FileCount.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>存储洞察的一级子文件夹占用行。</summary>
public sealed record StorageFolderUsageModel(
    string Name,
    string Path,
    long TotalBytes,
    long FileCount,
    long FolderCount,
    double Percent,
    bool IsRootFiles)
{
    public string SizeText => Formatting.Bytes(TotalBytes);
    public string PercentText => Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    public string CountText => $"{FileCount.ToString("N0", CultureInfo.InvariantCulture)} 个文件";
}

/// <summary>精确重复文件复核组；只展示与定位，不承载删除选择。</summary>
public sealed record StorageDuplicateGroupModel(
    long FileSize,
    string Sha256,
    IReadOnlyList<FileItemViewModel> Files)
{
    public int Count => Files.Count;
    public long TotalBytes => FileSize * Count;
    public long ReclaimableBytes => FileSize * Math.Max(0, Count - 1);
    public string EachSizeText => Formatting.Bytes(FileSize);
    public string ReclaimableText => Formatting.Bytes(ReclaimableBytes);
    public string ShortHash => Sha256.Length <= 12 ? Sha256 : Sha256[..12];
    public string SummaryText => $"{Count} 份 · 每份 {EachSizeText} · 可复核 {ReclaimableText}";
}

/// <summary>批量重命名预览行（Error 非空=整批阻断并标红）。</summary>
public sealed record BatchRenameRowModel(string Original, string Proposed, string? Error)
{
    public bool HasError => Error is not null;
}

/// <summary>端侧 AI 模型行：只报事实（装没装、占多大、缺几个文件），不下判断。</summary>
public sealed record AiModelStatusModel(
    string Id,
    string Name,
    string Purpose,
    string License,
    int ApproxSizeMb,
    bool Installed,
    bool Partial,
    int MissingFileCount,
    long InstalledBytes)
{
    public string SizeText => Installed
        ? Formatting.Bytes(InstalledBytes)
        : $"约 {ApproxSizeMb.ToString(CultureInfo.InvariantCulture)} MB";

    public string StatusText => Installed
        ? $"已就绪 · {Formatting.Bytes(InstalledBytes)}"
        : Partial
            ? $"下载不完整 · 缺 {MissingFileCount.ToString(CultureInfo.InvariantCulture)} 个文件"
            : "未安装";

    public string LicenseText => $"许可 {License}";

    /// <summary>没装或没装全都算「还没准备好」——设置页据此上色，不说「可用」。</summary>
    public bool NeedsAttention => !Installed;
}
