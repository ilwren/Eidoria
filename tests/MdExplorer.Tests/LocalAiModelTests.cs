using MdExplorer.Infrastructure.Ai;
using MdExplorer.UI.Services;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.37：端侧 AI 的模型分发。这一版不跑推理，只保证「清单、下载脚本、应用」三方对同一份定义，
/// 并且状态汇报永远说真话（没装就是没装，装一半就说缺几个文件）。
/// </summary>
public sealed class LocalAiModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-ai-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EmbeddedManifestDescribesEveryModelWithLicenseAndFiles()
    {
        var models = AiModelManifest.LoadEmbedded();

        Assert.NotEmpty(models);
        Assert.Equal(models.Count, models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var model in models)
        {
            Assert.NotEmpty(model.Name);
            Assert.NotEmpty(model.Purpose);
            Assert.NotEmpty(model.License);
            Assert.StartsWith("http", model.LicenseUrl, StringComparison.Ordinal);
            Assert.Contains('/', model.Source);
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            Assert.Equal("onnx", model.Runtime);
            Assert.True(model.ApproxSizeMb > 0);
            Assert.NotEqual(
                model.Id is "image-vlm-smol" or "audio-asr-tiny" or "audio-classify-yamnet",
                model.DownloadByDefault);
            Assert.NotEmpty(model.Files);
            // 文本模型一般单 ONNX；VLM/ASR 会拆成多图，所以只要求至少一个权重图。
            Assert.Contains(model.Files, file => file.EndsWith(".onnx", StringComparison.Ordinal));
            if (model.Id == "audio-classify-yamnet")
                Assert.DoesNotContain(model.Files, file => file.EndsWith("tokenizer.json", StringComparison.Ordinal));
            else
                Assert.Contains(model.Files, file => file.EndsWith("tokenizer.json", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("model|a|b|c|d|e|f|0000000000000000000000000000000000000000|onnx")] // model 少列
    [InlineData("file|nope|x.onnx")]                                              // file 指向未知模型
    [InlineData("model|a|b|c|MIT|http://x|o/r|0000000000000000000000000000000000000000|onnx|1|true\nmodel|a|b|c|MIT|http://x|o/r|0000000000000000000000000000000000000000|onnx|1|true")] // id 重复
    [InlineData("model|a|b|c|MIT|http://x|o/r|0000000000000000000000000000000000000000|onnx|1|maybe")] // 默认下载字段
    [InlineData("model|a|b|c|MIT|http://x|o/r|main|onnx|1|false")]                 // 不接受浮动 revision
    [InlineData("weird|a")]                                                       // 未知类型
    public void ManifestRejectsBrokenLines(string text) =>
        Assert.Throws<InvalidDataException>(() => AiModelManifest.Parse(text));

    [Fact]
    public void CatalogReportsMissingPartialAndInstalled()
    {
        var manifest = AiModelManifest.LoadEmbedded();
        var model = manifest[0];
        var catalog = new AiModelCatalog(Path.Combine(_root, "models"), manifest);

        // 1) 一个都没有：全部未安装，且不谎报部分下载。
        var empty = catalog.Scan();
        Assert.All(empty, status =>
        {
            Assert.False(status.Installed);
            Assert.False(status.Partial);
            Assert.Equal(status.TotalFiles, status.MissingFiles.Count);
        });

        // 2) 只落了一个文件：算「下载不完整」，缺的文件要数得出来。
        var directory = Path.Combine(catalog.ModelsDirectory, model.Id);
        var first = Path.Combine(directory, model.Files[0].Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllBytes(first, new byte[1024]);
        var partial = catalog.Scan().Single(status => status.Definition.Id == model.Id);
        Assert.False(partial.Installed);
        Assert.True(partial.Partial);
        Assert.Equal(1, partial.PresentFiles);
        Assert.Equal(model.Files.Count - 1, partial.MissingFiles.Count);
        Assert.Equal(1024, partial.InstalledBytes);

        // 3) 全部落盘：就绪，字节数按实际大小累加。
        foreach (var file in model.Files.Skip(1))
        {
            var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[2048]);
        }

        var installed = catalog.Scan().Single(status => status.Definition.Id == model.Id);
        Assert.True(installed.Installed);
        Assert.False(installed.Partial);
        Assert.Empty(installed.MissingFiles);
        Assert.Equal(1024 + 2048 * (model.Files.Count - 1), installed.InstalledBytes);
    }

    [Fact]
    public void CatalogDirectoryFollowsEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable(AiModelCatalog.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                AiModelCatalog.DirectoryEnvironmentVariable, Path.Combine(_root, "custom"));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(_root, "custom")),
                AiModelCatalog.ResolveDirectory(Path.Combine(_root, "data")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AiModelCatalog.DirectoryEnvironmentVariable, previous);
        }

        Assert.Equal(
            Path.Combine(_root, "data", "models"),
            AiModelCatalog.ResolveDirectory(Path.Combine(_root, "data")));
    }

    [Fact]
    public async Task ShellReportsMissingModelsAndOffersTheRightCommand()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var start = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        var shell = AppHost.CreateShell(AppHost.Build(dataDirectory: data, startFolder: start));

        Assert.False(shell.EnableLocalAi); // 默认关：端侧 AI 不替用户做决定
        Assert.NotEmpty(shell.LocalAiModels);
        Assert.False(shell.HasLocalAiModel);
        Assert.Contains("尚未下载", shell.LocalAiStatusText, StringComparison.Ordinal);
        Assert.Contains("fetch-ai-models", shell.LocalAiDownloadCommandText, StringComparison.Ordinal);
        Assert.EndsWith(
            OperatingSystem.IsWindows() ? "fetch-ai-models.ps1" : "fetch-ai-models.sh",
            shell.LocalAiDownloadCommandText,
            StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(data, "models"), shell.LocalAiModelsDirectoryText);

        // 开关落盘：下次启动应当还是开着的。
        shell.EnableLocalAi = true;
        Assert.True(shell.SettingsStore.Settings.EnableLocalAi);
        await Task.CompletedTask;
    }
}
