namespace MdExplorer.Domain;

/// <summary>自动化节点类型。控制流只允许有界的单个匹配数量条件；不执行任意脚本、循环或嵌套分支。</summary>
public enum AutomationNodeKind
{
    CurrentFolder,
    KindFilter,
    NameContains,
    TransferToSubfolder,
    SystemNotification,
    // Append new kinds; persisted settings use numeric enum values in existing installations.
    MatchCountCondition,
}

public enum AutomationTransferMode
{
    Copy,
    Move,
}

/// <summary>
/// 一个可持久化自动化节点。参数按节点类型显式分开保存，避免用无类型字典在 JSON 中
/// 漂移；坐标只服务节点画布，执行顺序只由连线决定。
/// </summary>
public sealed class AutomationNodeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AutomationNodeKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>类型筛选目标；null 表示任意文件类型。Folder 在执行校验中不合法，因为来源只取顶层文件。</summary>
    public FileKind? FilterKind { get; set; }

    /// <summary>名称包含筛选（不区分大小写）；空字符串表示不按名称收窄。</summary>
    public string NameQuery { get; set; } = string.Empty;

    /// <summary>MatchCountCondition 节点阈值：匹配项数达到此值时走 matched 分支，否则走 unmatched 分支。</summary>
    public int MinimumMatchCount { get; set; } = 1;

    /// <summary>目标子文件夹名，只能是当前文件夹下的一级名称，不能包含路径分隔符。</summary>
    public string DestinationSubfolder { get; set; } = "已整理";

    public AutomationTransferMode TransferMode { get; set; } = AutomationTransferMode.Copy;

    public string NotificationTitle { get; set; } = "自动化完成";

    /// <summary>支持 {count}、{succeeded}、{failed}、{destination} 四个有界占位符。</summary>
    public string NotificationBody { get; set; } = "已处理 {succeeded}/{count} 个文件到 {destination}。";
}

public static class AutomationPortIds
{
    public const string Default = "default";
    public const string Matched = "matched";
    public const string Unmatched = "unmatched";
}

public sealed class AutomationConnectionDefinition
{
    public string SourceNodeId { get; set; } = string.Empty;
    /// <summary>旧版线性连线默认为 default；条件节点使用 matched / unmatched。</summary>
    public string SourcePortId { get; set; } = AutomationPortIds.Default;
    public string TargetNodeId { get; set; } = string.Empty;
}

/// <summary>单个自动化工作流的完整定义。当前产品只持久化一条工作流，但模型不把节点数量写死。</summary>
public sealed class AutomationWorkflowDefinition
{
    public string Name { get; set; } = "整理当前文件夹";
    public List<AutomationNodeDefinition> Nodes { get; set; } = [];
    public List<AutomationConnectionDefinition> Connections { get; set; } = [];

    public static AutomationWorkflowDefinition CreateDefault() => CreateExample("images");

    /// <summary>Built-in starter graphs remain ordinary, editable workflow data; loading one replaces the current graph only after confirmation.</summary>
    public static AutomationWorkflowDefinition CreateExample(string exampleId)
    {
        if (exampleId == "threshold")
            return CreateThresholdExample();

        var (name, filter, nameQuery, destination, mode) = exampleId switch
        {
            "images" => ("整理图片", FileKind.Image, string.Empty, "图片整理", AutomationTransferMode.Copy),
            "videos" => ("归档视频", FileKind.Video, string.Empty, "视频归档", AutomationTransferMode.Copy),
            "documents" => ("收纳文档", FileKind.Document, "发票", "发票归档", AutomationTransferMode.Copy),
            "code" => ("整理代码说明", FileKind.Code, "README", "代码说明", AutomationTransferMode.Copy),
            _ => throw new ArgumentOutOfRangeException(nameof(exampleId), "未知的自动化示例。"),
        };

        var nodes = new List<AutomationNodeDefinition>
        {
            new() { Id = "source", Kind = AutomationNodeKind.CurrentFolder, X = 24, Y = 128 },
            new() { Id = "kind-filter", Kind = AutomationNodeKind.KindFilter, X = 250, Y = 128, FilterKind = filter },
        };
        var connections = new List<AutomationConnectionDefinition>
        {
            new() { SourceNodeId = "source", TargetNodeId = "kind-filter" },
        };
        var previous = "kind-filter";

        if (!string.IsNullOrEmpty(nameQuery))
        {
            nodes.Add(new AutomationNodeDefinition
            {
                Id = "name-filter",
                Kind = AutomationNodeKind.NameContains,
                X = 476,
                Y = 128,
                NameQuery = nameQuery,
            });
            connections.Add(new AutomationConnectionDefinition { SourceNodeId = previous, TargetNodeId = "name-filter" });
            previous = "name-filter";
        }

        var transferX = nameQuery.Length == 0 ? 476 : 702;
        nodes.Add(new AutomationNodeDefinition
        {
            Id = "transfer",
            Kind = AutomationNodeKind.TransferToSubfolder,
            X = transferX,
            Y = 128,
            DestinationSubfolder = destination,
            TransferMode = mode,
        });
        connections.Add(new AutomationConnectionDefinition { SourceNodeId = previous, TargetNodeId = "transfer" });
        nodes.Add(new AutomationNodeDefinition
        {
            Id = "notify",
            Kind = AutomationNodeKind.SystemNotification,
            X = transferX + 226,
            Y = 128,
            NotificationTitle = "自动化完成",
            NotificationBody = "已处理 {succeeded}/{count} 个文件到 {destination}。",
        });
        connections.Add(new AutomationConnectionDefinition { SourceNodeId = "transfer", TargetNodeId = "notify" });

        return new AutomationWorkflowDefinition { Name = name, Nodes = nodes, Connections = connections };
    }

    private static AutomationWorkflowDefinition CreateThresholdExample()
    {
        var nodes = new List<AutomationNodeDefinition>
        {
            new() { Id = "source", Kind = AutomationNodeKind.CurrentFolder, X = 24, Y = 128 },
            new() { Id = "kind-filter", Kind = AutomationNodeKind.KindFilter, X = 250, Y = 128, FilterKind = FileKind.Image },
            new() { Id = "match-count", Kind = AutomationNodeKind.MatchCountCondition, X = 476, Y = 128, MinimumMatchCount = 2 },
            new() { Id = "transfer", Kind = AutomationNodeKind.TransferToSubfolder, X = 702, Y = 48, DestinationSubfolder = "图片整理" },
            new()
            {
                Id = "notify",
                Kind = AutomationNodeKind.SystemNotification,
                X = 928,
                Y = 128,
                NotificationTitle = "图片整理结果",
                NotificationBody = "处理 {succeeded}/{count} 个文件到 {destination}。",
            },
        };
        var connections = new List<AutomationConnectionDefinition>
        {
            new() { SourceNodeId = "source", TargetNodeId = "kind-filter" },
            new() { SourceNodeId = "kind-filter", TargetNodeId = "match-count" },
            new()
            {
                SourceNodeId = "match-count",
                SourcePortId = AutomationPortIds.Matched,
                TargetNodeId = "transfer",
            },
            new()
            {
                SourceNodeId = "match-count",
                SourcePortId = AutomationPortIds.Unmatched,
                TargetNodeId = "notify",
            },
            new() { SourceNodeId = "transfer", TargetNodeId = "notify" },
        };
        return new AutomationWorkflowDefinition
        {
            Name = "至少两张图片才归档",
            Nodes = nodes,
            Connections = connections,
        };
    }
}
