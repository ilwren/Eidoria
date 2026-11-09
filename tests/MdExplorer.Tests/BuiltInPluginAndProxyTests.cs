using System.Security.Cryptography;
using MdExplorer.Infrastructure.Ai;
using MdExplorer.Infrastructure.Plugins;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.61 三件事的收口测试：
/// ① 内置示例扩展包必须过自己定的所有 MEPX v1 校验；
/// ② 生成要确定（同清单 → 同字节 → 同哈希），否则「SHA-256 给你看」是假的；
/// ③ AI 下载代理把任意用户输入收口到三档，自定义地址不合法时退回系统而不是悄悄断网；
/// ④ 品牌更名后应用数据目录从 MdExplorer 迁到 Eidoria，迁移失败绝不动旧数据。
/// </summary>
public sealed class BuiltInPluginAndProxyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eidoria-builtin-tests", Guid.NewGuid().ToString("N"));

    public BuiltInPluginAndProxyTests() => Directory.CreateDirectory(_root);

    // ── 内置插件包 ────────────────────────────────────────────────────

    [Fact]
    public async Task BuiltInPackages_PassTheSameValidationPipelineAsImportedOnes()
    {
        var catalog = new PluginPackageCatalog(_root);
        foreach (var definition in BuiltInPluginPackages.Definitions)
        {
            var bytes = BuiltInPluginPackages.CreatePackageBytes(definition);
            var candidate = await catalog.InspectAsync(new MemoryStream(bytes, writable: false), CancellationToken.None);
            Assert.Equal(definition.Id, candidate.Manifest.Id);
            Assert.Equal(definition.SeedColor, candidate.Manifest.Theme!.SeedColor);
            Assert.Equal(["theme"], candidate.Manifest.Capabilities);
        }
    }

    [Fact]
    public void BuiltInPackages_AreDeterministicAcrossRuns()
    {
        foreach (var definition in BuiltInPluginPackages.Definitions)
        {
            var first = BuiltInPluginPackages.CreatePackageBytes(definition);
            var second = BuiltInPluginPackages.CreatePackageBytes(definition);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(first)),
                Convert.ToHexString(SHA256.HashData(second)));
        }
    }

    [Fact]
    public async Task BuiltInPackages_InstallAndRescanThroughTheRealCatalog()
    {
        var catalog = new PluginPackageCatalog(_root);
        foreach (var definition in BuiltInPluginPackages.Definitions)
        {
            var bytes = BuiltInPluginPackages.CreatePackageBytes(definition);
            var candidate = await catalog.InspectAsync(new MemoryStream(bytes, writable: false), CancellationToken.None);
            await catalog.InstallAsync(candidate, CancellationToken.None);
        }

        var snapshot = catalog.Scan();
        Assert.Equal(BuiltInPluginPackages.Definitions.Count, snapshot.Packages.Count);
        Assert.Empty(snapshot.Issues);
        // builtin.* 是宿主保留域，内置示例必须走对外可见的命名空间，不会撞上保留检查。
        Assert.All(snapshot.Packages, package =>
            Assert.StartsWith("eidoria.samples.", package.Manifest.Id, StringComparison.Ordinal));
    }

    // ── 代理收口 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "System")]
    [InlineData("", "System")]
    [InlineData("system", "System")]        // 大小写不合法 → 收
    [InlineData(" Custom ", "Custom")]      // 带空白输入修剪后按 Custom 认
    [InlineData("None", "None")]
    [InlineData("Custom", "Custom")]
    public void ProxyMode_NormalizesUnknownInputToSystem(string? input, string expected)
    {
        Assert.Equal(expected, AiProxyPlan.NormalizeMode(input));
    }

    [Fact]
    public void ProxyPlan_None_AlwaysDirect()
    {
        var plan = AiProxyPlan.Resolve(new AppSettings { AiModelProxyMode = "None", AiModelProxyAddress = "http://127.0.0.1:7890" });
        Assert.False(plan.UseProxy);
        Assert.Null(plan.CreateWebProxy());
    }

    [Fact]
    public void ProxyPlan_CustomAcceptsBareHostPort()
    {
        var plan = AiProxyPlan.Resolve(new AppSettings { AiModelProxyMode = "Custom", AiModelProxyAddress = "127.0.0.1:7890" });
        Assert.True(plan.UseProxy);
        Assert.Equal("http://127.0.0.1:7890/", plan.Address);
        Assert.NotNull(plan.CreateWebProxy());
    }

    [Theory]
    [InlineData("")]                 // 空地址
    [InlineData("not a uri")]        // 非法
    [InlineData("ftp://1.2.3.4:21")] // 非 http(s)
    public void ProxyPlan_InvalidCustomAddressFallsBackToSystem(string address)
    {
        var plan = AiProxyPlan.Resolve(new AppSettings { AiModelProxyMode = "Custom", AiModelProxyAddress = address });
        Assert.Same(AiProxyPlan.System, plan);
    }

    // ── 数据目录品牌迁移 ──────────────────────────────────────────────

    [Fact]
    public void DataDirectory_MigratesFromLegacyName()
    {
        var legacy = Path.Combine(_root, AppHostDataDirProbe.LegacyDirName);
        var next = Path.Combine(_root, AppHost.DataDirectoryName);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        var resolved = AppHost.ResolveDataDirectory(next, legacy);
        Assert.Equal(next, resolved);
        Assert.True(File.Exists(Path.Combine(next, "settings.json")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void DataDirectory_KeepsNextWhenBothExist_AndLeavesLegacyAlone()
    {
        var legacy = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var next = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(next);
        Assert.Equal(next, AppHost.ResolveDataDirectory(next, legacy));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void DataDirectory_NoLegacy_StaysOnNext()
    {
        var next = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Assert.Equal(next, AppHost.ResolveDataDirectory(next, legacy));
        Assert.False(Directory.Exists(next)); // 只解析不创建；创建是 AppHost.Build 的职责
    }

    // 迁移真实目录名（MdExplorer）的常量不公开，测试侧自己持有代号，避免“把测试也改名”时漏报。
    private static class AppHostDataDirProbe
    {
        public const string LegacyDirName = "MdExplorer";
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
