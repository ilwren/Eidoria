using System.Text;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>
/// 端侧 AI 模型清单里的一条。清单本身是 <c>scripts/ai-models.manifest</c>（纯文本、<c>|</c> 分隔），
/// 由下载脚本与应用共用：脚本用 bash/PowerShell 读，应用把它作为嵌入资源读，
/// 两边看到的是同一份定义，不会出现「脚本下的和程序找的不是一回事」。
/// </summary>
public sealed record AiModelDefinition(
    string Id,
    string Name,
    string Purpose,
    string License,
    string LicenseUrl,
    string Source,
    string Revision,
    string Runtime,
    int ApproxSizeMb,
    bool DownloadByDefault,
    IReadOnlyList<string> Files);

/// <summary>一个模型在本机上的实际情况：装没装、占多少、缺哪些文件。</summary>
public sealed record AiModelStatus(
    AiModelDefinition Definition,
    bool Installed,
    long InstalledBytes,
    int PresentFiles,
    IReadOnlyList<string> MissingFiles)
{
    public int TotalFiles => Definition.Files.Count;

    /// <summary>装了一半（权限中断、磁盘满）也要如实说出来，不能报「未安装」了事。</summary>
    public bool Partial => !Installed && PresentFiles > 0;

    public bool Missing => PresentFiles == 0;
}

/// <summary>
/// 清单解析。刻意只做最小校验：清单是仓库内的数据文件，写错应当在开发期就炸掉，
/// 而不是让用户看到半张残缺的模型表。
/// </summary>
public static class AiModelManifest
{
    /// <summary>嵌入资源名，与 csproj 的 <c>LogicalName</c> 一致。</summary>
    public const string ResourceName = "MdExplorer.Ai.Models.manifest";

    private const int ModelColumns = 11;
    private const int FileColumns = 3;

    public static IReadOnlyList<AiModelDefinition> LoadEmbedded()
    {
        using var stream = typeof(AiModelManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"程序集里找不到模型清单资源：{ResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>解析清单文本；格式错误抛 <see cref="InvalidDataException"/> 并指出行号。</summary>
    public static IReadOnlyList<AiModelDefinition> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var models = new List<AiModelDefinition>();
        var byId = new Dictionary<string, (int Index, List<string> Files)>(StringComparer.Ordinal);
        var lineNumber = 0;

        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('|');
            if (parts[0] == "model")
            {
                if (parts.Length != ModelColumns)
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行格式不对（model 需要 {ModelColumns} 列）。");
                var id = parts[1];
                if (!System.Text.RegularExpressions.Regex.IsMatch(parts[7], "^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行 revision 必须是 40 位小写十六进制 SHA。");
                if (!bool.TryParse(parts[10], out var downloadByDefault))
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行默认下载字段必须是 true/false。");
                if (id.Length == 0)
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行缺少模型 id。");
                if (byId.ContainsKey(id))
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行模型 id 重复：{id}。");

                byId[id] = (models.Count, []);
                models.Add(new AiModelDefinition(
                    id,
                    parts[2],
                    parts[3],
                    parts[4],
                    parts[5],
                    parts[6],
                    parts[7],
                    parts[8],
                    int.TryParse(parts[9], out var mb) ? mb : 0,
                    downloadByDefault,
                    []));
            }
            else if (parts[0] == "file")
            {
                if (parts.Length != FileColumns)
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行格式不对（file 需要 {FileColumns} 列）。");
                if (!byId.TryGetValue(parts[1], out var entry))
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行的 file 指向了未知模型：{parts[1]}。");
                if (parts[2].Length == 0)
                    throw new InvalidDataException($"模型清单第 {lineNumber} 行的 file 缺少路径。");
                entry.Files.Add(parts[2]);
            }
            else
            {
                throw new InvalidDataException($"模型清单第 {lineNumber} 行有不认识的类型：{parts[0]}。");
            }
        }

        if (models.Count == 0)
            throw new InvalidDataException("模型清单里没有任何模型。");

        // 文件列表是解析完才填的，这里统一换成只读快照。
        for (var i = 0; i < models.Count; i++)
        {
            var files = byId[models[i].Id].Files;
            models[i] = models[i] with { Files = files.ToArray() };
        }

        return models;
    }
}

/// <summary>
/// 模型目录的扫描器。只做「文件在不在、多大」——不加载权重、不初始化运行时，
/// 所以设置页打开它也不会有任何推理开销。
/// </summary>
public sealed class AiModelCatalog
{
    /// <summary>覆盖模型目录的环境变量；与下载脚本共用。</summary>
    public const string DirectoryEnvironmentVariable = "MDEXPLORER_MODELS";

    private readonly IReadOnlyList<AiModelDefinition> _models;

    public AiModelCatalog(string modelsDirectory, IReadOnlyList<AiModelDefinition>? models = null)
    {
        ModelsDirectory = modelsDirectory;
        _models = models ?? AiModelManifest.LoadEmbedded();
    }

    public string ModelsDirectory { get; }

    public IReadOnlyList<AiModelDefinition> Models => _models;

    /// <summary>
    /// 默认目录：环境变量优先，其次是应用数据目录下的 <c>models</c>。
    /// 不往用户目录外写东西，也不扫描仓库外的路径。
    /// </summary>
    public static string ResolveDirectory(string dataDirectory)
    {
        var overridePath = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);
        return Path.Combine(dataDirectory, "models");
    }

    /// <summary>扫描全部模型当前状态（每个模型一个子目录，与下载脚本落盘布局一致）。</summary>
    public IReadOnlyList<AiModelStatus> Scan() => _models.Select(statusFor).ToArray();

    private AiModelStatus statusFor(AiModelDefinition model)
    {
        var root = Path.Combine(ModelsDirectory, model.Id);
        var missing = new List<string>();
        long bytes = 0;

        foreach (var file in model.Files)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0)
                bytes += info.Length;
            else
                missing.Add(file);
        }

        return new AiModelStatus(
            model,
            Installed: missing.Count == 0,
            InstalledBytes: bytes,
            PresentFiles: model.Files.Count - missing.Count,
            MissingFiles: missing);
    }
}
