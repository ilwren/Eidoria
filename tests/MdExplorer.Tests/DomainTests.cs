using MdExplorer.Application;
using MdExplorer.Domain;
using Xunit;

namespace MdExplorer.Tests;

public sealed class DomainTests
{
    [Fact]
    public void NaturalSort_PutsNumbersInNumericOrder()
    {
        var names = new[] { "file10.txt", "file2.txt", "File1.txt", "file02.txt" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(["File1.txt", "file2.txt", "file02.txt", "file10.txt"], names);
    }

    [Theory]
    [InlineData("photo.JPG", FileKind.Image)]
    [InlineData("movie.mkv", FileKind.Video)]
    [InlineData("song.flac", FileKind.Audio)]
    [InlineData("spec.pdf", FileKind.Document)]
    [InlineData("a.zip", FileKind.Archive)]
    [InlineData("Program.cs", FileKind.Code)]
    [InlineData("data.json", FileKind.Data)]
    [InlineData("README.md", FileKind.Text)]
    [InlineData("LICENSE", FileKind.Text)]
    [InlineData("mystery.qqq", FileKind.Unknown)]
    public void Classifier_MapsExtensions(string name, FileKind expected) =>
        Assert.Equal(expected, FileKindClassifier.Classify(name));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1500, "1.5 KB")]
    [InlineData(3_200_000, "3.2 MB")]
    [InlineData(1_400_000_000, "1.4 GB")]
    public void Formatting_Bytes(long bytes, string expected) =>
        Assert.Equal(expected, Formatting.Bytes(bytes));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("CON.txt")]
    [InlineData("name.")]
    public void NameValidator_Rejects(string name) => Assert.NotNull(NameValidator.Validate(name));

    [Theory]
    [InlineData("报告 v2.pdf")]
    [InlineData("normal-file_1.txt")]
    public void NameValidator_Accepts(string name) => Assert.Null(NameValidator.Validate(name));

    [Fact]
    public void PathSegments_BuildBreadcrumb()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var path = Path.Combine(root, "a", "b", "c");
        var segments = PathText.Segments(path);
        Assert.Equal(4, segments.Count);
        Assert.Equal("c", segments[^1].Name);
        Assert.Equal(path, segments[^1].FullPath);
    }

    [Fact]
    public void Ordering_FoldersFirstThenName()
    {
        var now = DateTimeOffset.Now;
        var items = new[]
        {
            new FileItem(new FileLocator("/x/b.txt"), "b.txt", FileKind.Text, 1, now, false, false, ".txt"),
            new FileItem(new FileLocator("/x/zz"), "zz", FileKind.Folder, null, now, false, false, null),
            new FileItem(new FileLocator("/x/a.txt"), "a.txt", FileKind.Text, 1, now, false, false, ".txt"),
        };
        var sorted = FileItemOrdering.Sort(items, SortField.Name, SortDirection.Ascending);
        Assert.Equal(["zz", "a.txt", "b.txt"], sorted.Select(s => s.Name));
    }
}
