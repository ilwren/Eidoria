using System.IO.Compression;
using System.Text;
using MdExplorer.Infrastructure.Plugins;
using Xunit;

namespace MdExplorer.Tests;

public sealed class PluginPackageCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mdexplorer-plugins", Guid.NewGuid().ToString("N"));
    private readonly PluginPackageCatalog _catalog;

    public PluginPackageCatalogTests()
    {
        Directory.CreateDirectory(_root);
        _catalog = new PluginPackageCatalog(_root);
    }

    [Fact]
    public async Task ThemePackage_InspectInstallScanAndRemove()
    {
        var packageBytes = CreatePackage(ThemeManifest);
        var candidate = await _catalog.InspectAsync(new MemoryStream(packageBytes), CancellationToken.None);

        Assert.Equal("community.calm-theme", candidate.Manifest.Id);
        Assert.Equal("#6750A4", candidate.Manifest.Theme!.SeedColor);
        Assert.Equal(64, candidate.Sha256.Length);

        var installed = await _catalog.InstallAsync(candidate, CancellationToken.None);
        Assert.True(File.Exists(installed.PackagePath));
        var snapshot = _catalog.Scan();
        var package = Assert.Single(snapshot.Packages);
        Assert.Empty(snapshot.Issues);
        Assert.Equal("Calm Surface", package.Manifest.Name);
        Assert.Equal(candidate.Sha256, package.Sha256);

        await _catalog.RemoveAsync(package.Manifest.Id, CancellationToken.None);
        Assert.Empty(_catalog.Scan().Packages);
    }

    [Fact]
    public async Task ManifestWithCodeEntry_IsRejected()
    {
        var bytes = CreatePackage(ThemeManifest, ("main.lua", "return true"));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _catalog.InspectAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Contains("唯一的 manifest.json", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"manifestVersion\":1,\"id\":\"community.first\",\"id\":\"community.second\",\"name\":\"x\",\"version\":\"1.0.0\",\"publisher\":\"x\",\"capabilities\":[\"theme\"],\"theme\":{\"seedColor\":\"#112233\"}}")]
    [InlineData("{\"manifestVersion\":1,\"id\":\"community.theme\",\"name\":\"x\",\"version\":\"1.0.0\",\"publisher\":\"x\",\"capabilities\":[\"theme\"],\"theme\":{\"seedColor\":\"#112233\",\"seedColor\":\"#445566\"}}")]
    public async Task DuplicateJsonProperties_AreRejected(string manifest)
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _catalog.InspectAsync(new MemoryStream(CreatePackage(manifest)), CancellationToken.None));
        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"manifestVersion\":1,\"id\":\"../escape\",\"name\":\"x\",\"version\":\"1.0.0\",\"publisher\":\"x\",\"capabilities\":[\"theme\"],\"theme\":{\"seedColor\":\"#112233\"}}")]
    [InlineData("{\"manifestVersion\":1,\"id\":\"community.external\",\"name\":\"x\",\"version\":\"1.0.0\",\"publisher\":\"x\",\"capabilities\":[\"process\"],\"theme\":{\"seedColor\":\"#112233\"}}")]
    [InlineData("{\"manifestVersion\":1,\"id\":\"community.unknown\",\"name\":\"x\",\"version\":\"1.0.0\",\"publisher\":\"x\",\"engine\":{\"kind\":\"lua\"},\"capabilities\":[\"theme\"],\"theme\":{\"seedColor\":\"#112233\"}}")]
    public async Task UnsafeOrUnknownManifest_IsRejected(string manifest)
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _catalog.InspectAsync(new MemoryStream(CreatePackage(manifest)), CancellationToken.None));
    }

    private static byte[] CreatePackage(string manifest, params (string Name, string Content)[] extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                writer.Write(manifest);
            foreach (var (name, content) in extraEntries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private const string ThemeManifest = """
        {"manifestVersion":1,"id":"community.calm-theme","name":"Calm Surface","version":"1.2.0","publisher":"MdExplorer Community","description":"A calm Material 3 seed color.","capabilities":["theme"],"theme":{"seedColor":"#6750A4"}}
        """;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
