using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Md3.Avalonia.Controls;
using MdExplorer.Domain;

namespace MdExplorer.UI.ViewModels;

/// <summary>文件行/卡片的展示模型：类型字形与着色、注脚、缩略图。</summary>
public sealed partial class FileItemViewModel(FileItem model) : ObservableObject
{
    public FileItem Model { get; } = model;

    public string Name => Model.Name;
    public string FullPath => Model.FullPath;
    public bool IsFolder => Model.IsFolder;
    public FileKind Kind => Model.Kind;

    public string MetaText => Model.IsFolder
        ? Formatting.DateTime(Model.LastWriteTime, DateTimeOffset.Now)
        : $"{Formatting.Bytes(Model.Length)} · {Formatting.DateTime(Model.LastWriteTime, DateTimeOffset.Now)}";

    /// <summary>列表/网格的单值注脚：文件只报大小，文件夹只报时间；完整事实留到详细信息与属性页。</summary>
    public string CompactMetaText => Model.IsFolder
        ? Formatting.DateTime(Model.LastWriteTime, DateTimeOffset.Now)
        : Formatting.Bytes(Model.Length);

    /// <summary>语义搜索命中附带余弦相似度；关键词搜索留空。</summary>
    public string? SearchScoreText { get; init; }
    public bool HasSearchScore => !string.IsNullOrWhiteSpace(SearchScoreText);

    /// <summary>搜索结果中展示父目录（普通浏览时由 Browser 关闭）。</summary>
    // 详细信息视图列（纯显示字符串；排序由 Browser 的 FileItemOrdering 管线负责）
    public string SizeText => Model.IsFolder ? "—" : Formatting.Bytes(Model.Length);
    public string KindText => FileKindClassifier.Describe(Model);
    /// <summary>详细信息列的短类型：扩展名优先，避免每行重复“JPG 文件 · 图像”。</summary>
    public string ShortKindText => Model.IsFolder
        ? "文件夹"
        : string.IsNullOrWhiteSpace(Model.Extension)
            ? KindText
            : Model.Extension.TrimStart('.').ToUpperInvariant();
    public string ModifiedText => Formatting.FullDateTime(Model.LastWriteTime);

    public string ParentText => Path.GetDirectoryName(Model.FullPath) ?? string.Empty;
    /// <summary>最近访问/收藏行只显示父文件夹名，完整路径保留在 ToolTip 与属性页。</summary>
    public string ParentNameText
    {
        get
        {
            var parent = ParentText.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(parent);
            return string.IsNullOrWhiteSpace(name) ? parent : name;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;
    public bool ShowGlyph => Thumbnail is null;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(ShowGlyph));

    public string? Glyph => Model.Kind switch
    {
        FileKind.Folder => MdSymbols.Folder,
        FileKind.Image => MdSymbols.Image,
        FileKind.Video => MdSymbols.Movie,
        FileKind.Audio => MdSymbols.MusicNote,
        FileKind.Document => MdExplorer.Domain.FileKindClassifier.IsPdf(Model) ? MdSymbols.PictureAsPdf : MdSymbols.Description,
        FileKind.Archive => MdSymbols.FolderZip,
        FileKind.Code => MdSymbols.Code,
        FileKind.Data => MdSymbols.DataObject,
        FileKind.Font => MdSymbols.FontDownload,
        FileKind.Executable => MdSymbols.Terminal,
        FileKind.Text => MdSymbols.TextSnippet,
        _ => MdSymbols.InsertDriveFile,
    };

    public IBrush GlyphBrush { get; } = new SolidColorBrush(Color.ForKind(model));

    /// <summary>与类型色同源的低透明底色：让裸图标变成有材质的图标井（列表/网格共用）。</summary>
    public IBrush GlyphBackgroundBrush { get; } = new SolidColorBrush(Color.ForKind(model)) { Opacity = 0.14 };

    private static class Color
    {
        public static Avalonia.Media.Color ForKind(FileItem item) => item.Kind switch
        {
            FileKind.Folder => Avalonia.Media.Color.FromRgb(0xD9, 0x9A, 0x1F),
            FileKind.Image => Avalonia.Media.Color.FromRgb(0x0E, 0x8F, 0x81),
            FileKind.Video => Avalonia.Media.Color.FromRgb(0x6D, 0x5B, 0xC2),
            FileKind.Audio => Avalonia.Media.Color.FromRgb(0xB0, 0x4F, 0x96),
            FileKind.Document => FileKindClassifier.IsPdf(item)
                ? Avalonia.Media.Color.FromRgb(0xC8, 0x3E, 0x36)
                : Avalonia.Media.Color.FromRgb(0x3E, 0x6E, 0xAE),
            FileKind.Archive => Avalonia.Media.Color.FromRgb(0x8A, 0x68, 0x33),
            FileKind.Code => Avalonia.Media.Color.FromRgb(0x2E, 0x8B, 0x47),
            FileKind.Data => Avalonia.Media.Color.FromRgb(0xA3, 0x74, 0x1B),
            FileKind.Font => Avalonia.Media.Color.FromRgb(0x54, 0x56, 0xC4),
            FileKind.Executable => Avalonia.Media.Color.FromRgb(0x44, 0x4C, 0x52),
            _ => Avalonia.Media.Color.FromRgb(0x66, 0x70, 0x78),
        };
    }
}

/// <summary>面包屑段。</summary>
public sealed record BreadcrumbSegment(string Name, string FullPath);

/// <summary>导航条目（抽屉中的已知目录/收藏/卷共用）。</summary>
public sealed record NavEntry(string Title, string? Glyph, string Path, string? Subtitle = null);
