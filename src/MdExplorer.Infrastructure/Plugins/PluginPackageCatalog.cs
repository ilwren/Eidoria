using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MdExplorer.Infrastructure.Plugins;

/// <summary>
/// Local, declarative .mepx package catalog. Version 1 intentionally accepts only a single JSON manifest
/// and the theme capability; package contents are data, never assemblies, scripts, executables, or XAML.
/// </summary>
public sealed class PluginPackageCatalog(string dataDirectory)
{
    public const string PackageExtension = ".mepx";
    public const int MaximumPackageBytes = 1024 * 1024;
    public const int MaximumManifestBytes = 32 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9.-]{2,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
    private readonly string _packageDirectory = Path.Combine(dataDirectory, "plugins");
    public string PackageDirectory => _packageDirectory;

    public async Task<PluginPackageCandidate> InspectAsync(Stream packageStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        var bytes = await ReadPackageBytesAsync(packageStream, cancellationToken).ConfigureAwait(false);
        var manifest = ReadManifest(bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new PluginPackageCandidate(bytes, manifest, hash);
    }

    public async Task<InstalledPluginPackage> InstallAsync(
        PluginPackageCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        // Re-validate the in-memory package before writing. Confirmation dialogs cannot turn stale or
        // externally-mutated metadata into an installed package, and no path from the manifest is used.
        var manifest = ReadManifest(candidate.PackageBytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(candidate.PackageBytes));
        if (!string.Equals(hash, candidate.Sha256, StringComparison.Ordinal) ||
            !string.Equals(manifest.Id, candidate.Manifest.Id, StringComparison.Ordinal))
            throw new InvalidDataException("插件包在检查后发生变化，请重新选择并检查。");

        Directory.CreateDirectory(_packageDirectory);
        var destination = Path.Combine(_packageDirectory, manifest.Id + PackageExtension);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, candidate.PackageBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }

        return new InstalledPluginPackage(manifest, hash, destination);
    }

    public PluginCatalogSnapshot Scan()
    {
        Directory.CreateDirectory(_packageDirectory);
        var packages = new List<InstalledPluginPackage>();
        var issues = new List<PluginPackageIssue>();
        foreach (var path in Directory.EnumerateFiles(_packageDirectory, "*" + PackageExtension)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumPackageBytes)
                    throw new InvalidDataException($"插件包大小必须介于 1 字节与 {MaximumPackageBytes / 1024} KB 之间。");
                var bytes = File.ReadAllBytes(path);
                var manifest = ReadManifest(bytes);
                packages.Add(new InstalledPluginPackage(
                    manifest,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    path));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or DecoderFallbackException)
            {
                issues.Add(new PluginPackageIssue(Path.GetFileName(path), ex.Message));
            }
        }

        // Refuse ambiguous IDs rather than silently picking whichever package happens to be enumerated first.
        foreach (var duplicate in packages.GroupBy(package => package.Manifest.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1).ToArray())
        {
            foreach (var package in duplicate)
            {
                packages.Remove(package);
                issues.Add(new PluginPackageIssue(Path.GetFileName(package.PackagePath),
                    $"插件标识「{duplicate.Key}」重复；请移除重复包后重新扫描。"));
            }
        }

        return new PluginCatalogSnapshot(
            packages.OrderBy(package => package.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            issues);
    }

    public Task RemoveAsync(string pluginId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidId(pluginId))
            throw new ArgumentException("插件标识无效。", nameof(pluginId));
        var path = Path.Combine(_packageDirectory, pluginId + PackageExtension);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReadPackageBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumPackageBytes)
                throw new InvalidDataException($"插件包过大（上限 {MaximumPackageBytes / 1024} KB）。");
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (buffer.Length == 0)
            throw new InvalidDataException("插件包为空。");
        return buffer.ToArray();
    }

    private static PluginManifest ReadManifest(byte[] packageBytes)
    {
        if (packageBytes.Length is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("插件包为空或超过 1 MB 上限。");

        using var package = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "manifest.json")
            throw new InvalidDataException("MEPX v1 只允许包含根目录下唯一的 manifest.json，不接受代码、资源或其他文件。");

        var entry = archive.Entries[0];
        if (entry.Length is <= 0 or > MaximumManifestBytes || entry.CompressedLength > MaximumPackageBytes)
            throw new InvalidDataException("插件 manifest 为空或超过 32 KB 上限。");

        using var entryStream = entry.Open();
        using var manifestBuffer = new MemoryStream((int)entry.Length);
        var block = new byte[4096];
        while (true)
        {
            var read = entryStream.Read(block, 0, block.Length);
            if (read == 0)
                break;
            if (manifestBuffer.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("插件 manifest 解压后超过 32 KB 上限。");
            manifestBuffer.Write(block, 0, read);
        }

        var manifestBytes = manifestBuffer.GetBuffer().AsSpan(0, checked((int)manifestBuffer.Length));
        EnsureNoDuplicateJsonProperties(manifestBytes);
        var json = StrictUtf8.GetString(manifestBytes);
        var manifest = JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest)
                       ?? throw new InvalidDataException("插件 manifest 不能为 null。");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void EnsureNoDuplicateJsonProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 8 });
        var containers = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.PropertyName:
                    var propertyNames = containers.Count > 0 ? containers.Peek() : null;
                    var propertyName = reader.GetString();
                    if (propertyNames is null || propertyName is null || !propertyNames.Add(propertyName))
                        throw new InvalidDataException("插件 manifest 含有重复或位置无效的 JSON 字段。");
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (containers.Count > 0)
                        containers.Pop();
                    break;
            }
        }
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (manifest.ManifestVersion != 1)
            throw new InvalidDataException("不支持此插件清单版本（当前支持 1）。");
        if (!IsValidId(manifest.Id) || manifest.Id.StartsWith("builtin.", StringComparison.Ordinal))
            throw new InvalidDataException("插件 id 必须为小写 ASCII 标识（3–64 个字符），且不能占用 builtin.* 保留命名空间。");
        if (!HasLength(manifest.Name, 1, 80))
            throw new InvalidDataException("插件名称必须为 1–80 个字符。");
        if (!VersionPattern.IsMatch(manifest.Version ?? string.Empty))
            throw new InvalidDataException("插件版本须为 semver 主.次.修格式，例如 1.0.0。");
        if (!HasLength(manifest.Publisher, 1, 100))
            throw new InvalidDataException("插件发布者必须为 1–100 个字符。");
        if (manifest.Description is { Length: > 500 })
            throw new InvalidDataException("插件说明不能超过 500 个字符。");
        if (manifest.Capabilities is null || manifest.Capabilities.Length != 1 ||
            !string.Equals(manifest.Capabilities[0], "theme", StringComparison.Ordinal))
            throw new InvalidDataException("当前 MEPX v1 只支持单一 theme 能力；插件不允许执行代码或声明其他能力。");
        if (manifest.Theme is null || !Regex.IsMatch(manifest.Theme.SeedColor ?? string.Empty,
                "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("theme.seedColor 必须是 #RRGGBB 格式。");
    }

    private static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    private static bool HasLength(string? value, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var length = value.Trim().Length;
        return length >= minimum && length <= maximum;
    }
}

public sealed class PluginManifest
{
    public int ManifestVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string[] Capabilities { get; init; } = [];
    public PluginThemeManifest? Theme { get; init; }
}

public sealed class PluginThemeManifest
{
    public string SeedColor { get; init; } = string.Empty;
}

public sealed class PluginPackageCandidate(byte[] packageBytes, PluginManifest manifest, string sha256)
{
    internal byte[] PackageBytes { get; } = packageBytes;
    public PluginManifest Manifest { get; } = manifest;
    public string Sha256 { get; } = sha256;
}

public sealed record InstalledPluginPackage(PluginManifest Manifest, string Sha256, string PackagePath);
public sealed record PluginPackageIssue(string FileName, string Message);
public sealed record PluginCatalogSnapshot(
    IReadOnlyList<InstalledPluginPackage> Packages,
    IReadOnlyList<PluginPackageIssue> Issues);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifest))]
internal partial class PluginManifestJsonContext : JsonSerializerContext
{
}
