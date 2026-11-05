using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Md3.Avalonia.Controls;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.UI.ViewModels;

/// <summary>压缩包工作台虚拟目录中的一行。文件夹行可以由显式条目或文件父链推导。</summary>
public sealed partial class ArchiveWorkbenchEntryViewModel : ObservableObject
{
    private ArchiveWorkbenchEntryViewModel(
        ArchiveEntryItem? entry,
        string virtualPath,
        string name,
        string parentPath,
        bool isFolder,
        long length,
        long compressedLength,
        DateTimeOffset lastWriteTime,
        int descendantFileCount,
        IReadOnlyList<string> descendantEntryNames,
        bool isChecked)
    {
        Entry = entry;
        VirtualPath = virtualPath;
        Name = name;
        ParentPath = parentPath;
        IsFolder = isFolder;
        Length = length;
        CompressedLength = compressedLength;
        LastWriteTime = lastWriteTime;
        DescendantFileCount = descendantFileCount;
        DescendantEntryNames = descendantEntryNames;
        _isChecked = isChecked;
    }

    public ArchiveEntryItem? Entry { get; }
    /// <summary>统一 / 分隔的虚拟路径；文件夹以 / 结尾。</summary>
    public string VirtualPath { get; }
    public string Name { get; }
    public string ParentPath { get; }
    public bool IsFolder { get; }
    public long Length { get; }
    public long CompressedLength { get; }
    public DateTimeOffset LastWriteTime { get; }
    public int DescendantFileCount { get; }
    /// <summary>文件行为自身规范化路径；文件夹行为全部后代文件路径。</summary>
    public IReadOnlyList<string> DescendantEntryNames { get; }

    [ObservableProperty] private bool _isChecked;

    public event EventHandler? CheckedChanged;

    partial void OnIsCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckOpacity));
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    public FileKind Kind => IsFolder
        ? FileKind.Folder
        : Entry?.Kind ?? FileKindClassifier.Classify(Name);

    public string? Glyph => Kind switch
    {
        FileKind.Folder => MdSymbols.Folder,
        FileKind.Image => MdSymbols.Image,
        FileKind.Video => MdSymbols.Movie,
        FileKind.Audio => MdSymbols.MusicNote,
        FileKind.Document => MdSymbols.Description,
        FileKind.Archive => MdSymbols.FolderZip,
        FileKind.Code => MdSymbols.Code,
        FileKind.Data => MdSymbols.DataObject,
        FileKind.Font => MdSymbols.FontDownload,
        FileKind.Executable => MdSymbols.Terminal,
        FileKind.Text => MdSymbols.TextSnippet,
        _ => MdSymbols.InsertDriveFile,
    };

    public double CheckOpacity => IsChecked ? 1 : 0.28;
    public string SizeText => Formatting.Bytes(Length);
    public string CompressedSizeText => Formatting.Bytes(CompressedLength);
    public double CompressionRatio => Length > 0 ? Math.Clamp(CompressedLength / (double)Length, 0, 1) : 0;
    public string CompressionRatioText =>
        Length > 0 ? (CompressionRatio * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
    public string ModifiedText => Formatting.DateTime(LastWriteTime, DateTimeOffset.Now);
    public string FullModifiedText => Formatting.FullDateTime(LastWriteTime);
    public string DisplayPath => IsFolder ? VirtualPath.TrimEnd('/') : VirtualPath;
    public string ParentText => string.IsNullOrEmpty(ParentPath) ? "压缩包根目录" : ParentPath.TrimEnd('/');
    public string MetaText => IsFolder
        ? $"{DescendantFileCount.ToString("N0", CultureInfo.InvariantCulture)} 个文件 · {ModifiedText}"
        : $"{ParentText} · {ModifiedText}";
    public string SizePairText => $"{SizeText} → {CompressedSizeText}";

    public static ArchiveWorkbenchEntryViewModel FromFile(ArchiveEntryItem entry, bool isChecked)
    {
        var virtualPath = entry.VirtualPath.TrimStart('/');
        return new ArchiveWorkbenchEntryViewModel(
            entry,
            virtualPath,
            EntryName(virtualPath),
            EntryParent(virtualPath),
            isFolder: false,
            entry.Length,
            entry.CompressedLength,
            entry.LastWriteTime,
            descendantFileCount: 1,
            descendantEntryNames: [virtualPath],
            isChecked);
    }

    public static ArchiveWorkbenchEntryViewModel FromFolder(
        string virtualPath,
        ArchiveEntryItem? explicitEntry,
        long length,
        long compressedLength,
        DateTimeOffset lastWriteTime,
        IReadOnlyList<string> descendantEntryNames,
        bool isChecked)
    {
        var normalized = virtualPath.TrimStart('/');
        if (!normalized.EndsWith('/')) normalized += "/";
        var trimmed = normalized.TrimEnd('/');
        return new ArchiveWorkbenchEntryViewModel(
            explicitEntry,
            normalized,
            EntryName(trimmed),
            EntryParent(trimmed),
            isFolder: true,
            length,
            compressedLength,
            lastWriteTime,
            descendantEntryNames.Count,
            descendantEntryNames,
            isChecked);
    }

    private static string EntryName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string EntryParent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[..(slash + 1)] : string.Empty;
    }
}

/// <summary>压缩包虚拟目录面包屑。压缩包本身是第一段，Path 为空表示包内根目录。</summary>
public sealed record ArchiveBreadcrumbModel(string Name, string Path)
{
    public bool IsArchiveRoot => Path.Length == 0;
    public string? Glyph => IsArchiveRoot ? MdSymbols.FolderZip : MdSymbols.Folder;
}
