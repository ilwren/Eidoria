using System.IO.Compression;
using System.Text;

namespace MdExplorer.Infrastructure.Plugins;

/// <summary>
/// 内置示例扩展包（v0.61）。
/// <para>
/// 扩展中心如果只支持「自己去找 .mepx 文件」就没法验证插件管线。这四个包由应用现场
/// 生成真实 MEPX v1 字节（单条目 ZIP + 严格 manifest），走与外部导入完全相同的
/// <see cref="PluginPackageCatalog.InspectAsync"/> 校验与安装管线——它既是给用户的
/// 开箱示例，也是插件解析/安装/应用逻辑的常驻回归台架。
/// </para>
/// <para>
/// 生成是确定性的：固定条目内容、固定 LastWriteTime（POSIX epoch），相同清单永远得到
/// 相同字节与 SHA-256，因此可以写进测试断言。id 走对外命名空间（不能占用
/// <c>builtin.*</c> 保留域），安装后与普通导入包一样可移除。
/// </para>
/// </summary>
public static class BuiltInPluginPackages
{
    private static readonly DateTimeOffset FixedEntryTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>开箱示例（四枚种子色主题）。</summary>
    public static IReadOnlyList<BuiltInPluginPackageDefinition> Definitions { get; } =
    [
        new(
            "eidoria.samples.mist-harbor",
            "雾屿蓝",
            "1.0.0",
            "低饱和蓝灰，长时间浏览不刺眼。",
            "#54708C"),
        new(
            "eidoria.samples.pine-light",
            "松露绿",
            "1.0.0",
            "以 MD3 基准绿色为种，深浅主题成对验证。",
            "#386A20"),
        new(
            "eidoria.samples.ink-violet",
            "黛紫",
            "1.0.0",
            "靠近 Material You 默认色系的紫色示例。",
            "#6750A4"),
        new(
            "eidoria.samples.amber-dusk",
            "琥珀夜",
            "1.0.0",
            "暖色种子：主要验证深色主题的色调表面。",
            "#7C4A1E"),
    ];

    /// <summary>按清单生成确定的 MEPX 字节；写文件、喂给 InspectAsync 均可。</summary>
    public static byte[] CreatePackageBytes(BuiltInPluginPackageDefinition definition)
    {
        var manifestJson = $$"""
            {
              "manifestVersion": 1,
              "id": "{{definition.Id}}",
              "name": "{{definition.Name}}",
              "version": "{{definition.Version}}",
              "publisher": "Eidoria 示例",
              "description": "{{definition.Description}}",
              "capabilities": ["theme"],
              "theme": { "seedColor": "{{definition.SeedColor}}" }
            }
            """;

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            // 固定时间戳：同一份清单的字节与哈希跨机器、跨时间完全一致（可测试、可对照）。
            entry.LastWriteTime = FixedEntryTimestamp;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(manifestJson);
        }

        return buffer.ToArray();
    }

    /// <summary>每个示例的字节副本（调用一次生成一次，调用方自行决定缓存）。</summary>
    public static IReadOnlyList<(BuiltInPluginPackageDefinition Definition, byte[] Bytes)> CreateAll() =>
        Definitions.Select(definition => (definition, CreatePackageBytes(definition))).ToArray();
}

/// <summary>一枚内置示例扩展的清单字段（与 MEPX v1 约束一一对应）。</summary>
public sealed record BuiltInPluginPackageDefinition(
    string Id, string Name, string Version, string Description, string SeedColor);
