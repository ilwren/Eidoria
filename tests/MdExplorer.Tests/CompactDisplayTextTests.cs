using MdExplorer.Domain;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>v0.12 样式对标：常驻列表只保留一个紧凑事实，完整信息仍可从完整属性取得。</summary>
public sealed class CompactDisplayTextTests
{
    private static FileItemViewModel Item(string path, FileKind kind, long? length, string? extension) =>
        new(new FileItem(
            FileLocator.FromPath(path),
            Path.GetFileName(path),
            kind,
            length,
            DateTimeOffset.Now,
            IsHidden: false,
            IsSymlink: false,
            Extension: extension));

    [Fact]
    public void FileRowsShowOnlySizeAndShortKindWhileRetainingFullFacts()
    {
        var path = Path.Combine(Path.GetTempPath(), "Photos", "IMG_01.jpg");
        var item = Item(path, FileKind.Image, 2048, ".jpg");

        Assert.Equal(item.SizeText, item.CompactMetaText);
        Assert.DoesNotContain('·', item.CompactMetaText);
        Assert.Equal("JPG", item.ShortKindText);
        Assert.Contains('·', item.MetaText);
        Assert.Equal("Photos", item.ParentNameText);
        Assert.Equal(Path.GetDirectoryName(path), item.ParentText);
    }

    [Fact]
    public void FolderRowsShowOnlyRelativeDateAndExtensionlessFilesKeepDescribedKind()
    {
        var folder = Item(Path.Combine(Path.GetTempPath(), "Photos", "Trips"), FileKind.Folder, null, null);
        var extensionless = Item(Path.Combine(Path.GetTempPath(), "README"), FileKind.Text, 12, null);

        Assert.False(string.IsNullOrWhiteSpace(folder.CompactMetaText));
        Assert.DoesNotContain('·', folder.CompactMetaText);
        Assert.Equal("文件夹", folder.ShortKindText);
        Assert.Equal(extensionless.KindText, extensionless.ShortKindText);
    }
}
