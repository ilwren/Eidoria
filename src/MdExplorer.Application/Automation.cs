using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Application;

/// <summary>自动化图校验结果；有效时同时给出从来源开始的确定性执行顺序。</summary>
public sealed record AutomationValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<AutomationNodeDefinition> OrderedNodes)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 自动化一次执行的聚合结果。文件操作结果与系统通知结果刻意分开：通知权限或桌面服务失败
/// 不得把已经落盘的复制/移动误报成失败，反过来文件失败时仍可按流程发送失败摘要。
/// </summary>
public sealed record AutomationExecutionResult(
    OperationOutcome Operation,
    IReadOnlyList<FileItem> MatchedFiles,
    string? DestinationPath,
    SystemNotificationResult? Notification,
    IReadOnlyList<string> ValidationErrors);

/// <summary>
/// 自动化图的纯校验器。支持旧版线性链，以及“来源 → 顺序筛选 → 匹配数条件 → 传输 / 安全终止 → 可选通知”的单条件 DAG。
/// 节点、连线、传输与条件数量均有上限；不接受循环、嵌套分支、任意脚本或任意目标路径。
/// </summary>
public static class AutomationWorkflowValidator
{
    public const int MaximumNodes = 16;
    public const int MaximumConnections = 20;

    public static AutomationValidation Validate(AutomationWorkflowDefinition? workflow)
    {
        var errors = new List<string>();
        if (workflow is null)
            return new AutomationValidation(["工作流为空。"], []);
        if (string.IsNullOrWhiteSpace(workflow.Name))
            errors.Add("工作流名称不能为空。");
        else if (workflow.Name.Trim().Length > 80)
            errors.Add("工作流名称过长（上限 80 字符）。");

        var nodes = workflow.Nodes ?? [];
        var connections = workflow.Connections ?? [];
        if (nodes.Count == 0)
        {
            errors.Add("工作流至少需要“当前文件夹”和“传输到子文件夹”两个节点。");
            return new AutomationValidation(errors, []);
        }
        if (nodes.Count > MaximumNodes)
            return new AutomationValidation([$"节点数量超过安全上限 {MaximumNodes}。"], []);
        if (connections.Count > MaximumConnections)
            return new AutomationValidation([$"连线数量超过安全上限 {MaximumConnections}。"], []);

        var nodeById = new Dictionary<string, AutomationNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id))
            {
                errors.Add("每个节点都必须有非空 ID。");
                continue;
            }
            if (!nodeById.TryAdd(node.Id, node))
                errors.Add($"节点 ID 重复：{node.Id}。");
        }
        if (nodeById.Count != nodes.Count)
            errors.Add("节点集合包含空节点、空 ID 或重复 ID。");

        var sourceNodes = nodes.Where(node => node?.Kind == AutomationNodeKind.CurrentFolder).ToArray();
        var transferNodes = nodes.Where(node => node?.Kind == AutomationNodeKind.TransferToSubfolder).ToArray();
        var conditionNodes = nodes.Where(node => node?.Kind == AutomationNodeKind.MatchCountCondition).ToArray();
        var notificationNodes = nodes.Where(node => node?.Kind == AutomationNodeKind.SystemNotification).ToArray();
        if (sourceNodes.Length != 1)
            errors.Add("工作流必须且只能包含一个“当前文件夹”节点。");
        if (transferNodes.Length != 1)
            errors.Add("工作流必须且只能包含一个“传输到子文件夹”节点。");
        if (conditionNodes.Length > 1)
            errors.Add("当前仅支持一个“匹配数量条件”节点。");
        if (notificationNodes.Length > 1)
            errors.Add("工作流最多只能包含一个“系统通知”节点。");

        foreach (var node in nodes)
        {
            if (node is null)
                continue;
            if (!Enum.IsDefined(node.Kind))
            {
                errors.Add($"节点 {node.Id} 的类型未知。");
                continue;
            }

            switch (node.Kind)
            {
                case AutomationNodeKind.KindFilter:
                    if (node.FilterKind == FileKind.Folder)
                        errors.Add("“当前文件夹”来源只处理顶层文件，类型筛选不能选择文件夹。");
                    else if (node.FilterKind is { } filterKind && !Enum.IsDefined(filterKind))
                        errors.Add("类型筛选包含未知文件类型。");
                    break;
                case AutomationNodeKind.NameContains:
                    if ((node.NameQuery ?? string.Empty).Length > 200)
                        errors.Add("名称筛选过长（上限 200 字符）。");
                    break;
                case AutomationNodeKind.MatchCountCondition:
                    if (node.MinimumMatchCount is < 1 or > AutomationWorkflowExecutor.MaxTopLevelFiles)
                        errors.Add($"匹配数量条件必须在 1 到 {AutomationWorkflowExecutor.MaxTopLevelFiles} 之间。");
                    break;
                case AutomationNodeKind.TransferToSubfolder:
                    var destinationError = NameValidator.Validate(node.DestinationSubfolder?.Trim());
                    if (destinationError is not null)
                        errors.Add($"目标子文件夹名称不合法：{destinationError}。");
                    if (!Enum.IsDefined(node.TransferMode))
                        errors.Add("传输节点包含未知模式。");
                    break;
                case AutomationNodeKind.SystemNotification:
                    ValidateNotificationText(node.NotificationTitle, "通知标题", 120, requireNonEmpty: true, errors);
                    ValidateNotificationText(node.NotificationBody, "通知正文", 500, requireNonEmpty: false, errors);
                    break;
            }
        }

        var outgoing = nodeById.Keys.ToDictionary(id => id, _ => new List<AutomationConnectionDefinition>(), StringComparer.Ordinal);
        var incoming = nodeById.Keys.ToDictionary(id => id, _ => new List<AutomationConnectionDefinition>(), StringComparer.Ordinal);
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (connection is null || string.IsNullOrWhiteSpace(connection.SourceNodeId) ||
                string.IsNullOrWhiteSpace(connection.TargetNodeId))
            {
                errors.Add("连线两端都必须引用节点。");
                continue;
            }
            if (!nodeById.TryGetValue(connection.SourceNodeId, out var source) ||
                !nodeById.TryGetValue(connection.TargetNodeId, out _))
            {
                errors.Add($"连线引用了不存在的节点：{connection.SourceNodeId} → {connection.TargetNodeId}。");
                continue;
            }
            if (connection.SourceNodeId == connection.TargetNodeId)
            {
                errors.Add("节点不能连接到自身。");
                continue;
            }

            var portId = string.IsNullOrWhiteSpace(connection.SourcePortId)
                ? AutomationPortIds.Default
                : connection.SourcePortId;
            var validPort = source.Kind == AutomationNodeKind.MatchCountCondition
                ? portId is AutomationPortIds.Matched or AutomationPortIds.Unmatched
                : portId == AutomationPortIds.Default;
            if (!validPort)
            {
                errors.Add($"节点 {source.Id} 的输出端口无效：{portId}。");
                continue;
            }

            var edgeKey = $"{connection.SourceNodeId}\n{portId}\n{connection.TargetNodeId}";
            if (!seenEdges.Add(edgeKey))
            {
                errors.Add($"连线重复：{connection.SourceNodeId} → {connection.TargetNodeId}。");
                continue;
            }
            outgoing[connection.SourceNodeId].Add(connection);
            incoming[connection.TargetNodeId].Add(connection);
        }

        foreach (var node in nodeById.Values)
        {
            var nodeOutgoing = outgoing[node.Id];
            if (node.Kind != AutomationNodeKind.MatchCountCondition && nodeOutgoing.Count > 1)
                errors.Add($"节点 {node.Id} 最多只能有一条出线。");
            if (node.Kind == AutomationNodeKind.MatchCountCondition &&
                nodeOutgoing.GroupBy(edge => edge.SourcePortId, StringComparer.Ordinal).Any(group => group.Count() > 1))
                errors.Add($"条件节点 {node.Id} 的每个分支最多只能有一条出线。");

            var maxIncoming = node.Kind == AutomationNodeKind.SystemNotification && conditionNodes.Length == 1 ? 2 : 1;
            if (incoming[node.Id].Count > maxIncoming)
                errors.Add($"节点 {node.Id} 的入线数量超过上限 {maxIncoming}。");
        }

        var ordered = TopologicalOrder(nodeById, outgoing, incoming, errors);
        if (sourceNodes.Length == 1 && nodeById.ContainsKey(sourceNodes[0].Id))
        {
            var sourceId = sourceNodes[0].Id;
            if (incoming[sourceId].Count > 0)
                errors.Add("“当前文件夹”必须是流程起点，不能有入线。");

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            pending.Enqueue(sourceId);
            while (pending.TryDequeue(out var current))
            {
                if (!reachable.Add(current))
                    continue;
                foreach (var edge in outgoing[current])
                    pending.Enqueue(edge.TargetNodeId);
            }
            if (reachable.Count != nodeById.Count)
                errors.Add("所有节点都必须从“当前文件夹”可达；不接受孤立节点或隐藏执行分支。");
        }

        if (conditionNodes.Length == 0)
            ValidateLinearGraph(nodes, nodeById, outgoing, incoming, sourceNodes, transferNodes, notificationNodes, errors);
        else
            ValidateConditionalGraph(nodes, nodeById, outgoing, incoming, sourceNodes, transferNodes, conditionNodes, notificationNodes, errors);

        return errors.Count == 0
            ? new AutomationValidation([], ordered)
            : new AutomationValidation(errors.Distinct().ToArray(), []);
    }

    private static void ValidateLinearGraph(
        IReadOnlyList<AutomationNodeDefinition> nodes,
        IReadOnlyDictionary<string, AutomationNodeDefinition> nodeById,
        IReadOnlyDictionary<string, List<AutomationConnectionDefinition>> outgoing,
        IReadOnlyDictionary<string, List<AutomationConnectionDefinition>> incoming,
        AutomationNodeDefinition[] sources,
        AutomationNodeDefinition[] transfers,
        AutomationNodeDefinition[] notifications,
        List<string> errors)
    {
        if (nodes.Any(node => node is not null && node.Kind == AutomationNodeKind.MatchCountCondition))
            return;
        if (sources.Length != 1 || transfers.Length != 1)
            return;
        var ordered = new List<AutomationNodeDefinition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = sources[0];
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                errors.Add("工作流包含循环连线。");
                break;
            }
            ordered.Add(current);
            if (outgoing[current.Id].Count == 0)
                break;
            if (outgoing[current.Id].Count != 1 ||
                !nodeById.TryGetValue(outgoing[current.Id][0].TargetNodeId, out var next))
                break;
            current = next;
        }
        if (ordered.Count != nodeById.Count)
        {
            errors.Add("无条件工作流必须是一条从“当前文件夹”开始的单链。");
            return;
        }

        if (incoming[sources[0].Id].Count != 0)
            errors.Add("“当前文件夹”必须是流水线起点，不能有入线。");
        var transferIndex = ordered.FindIndex(node => node.Kind == AutomationNodeKind.TransferToSubfolder);
        if (transferIndex < 1)
            errors.Add("传输节点必须连接在“当前文件夹”之后。");
        else
        {
            for (var i = 1; i < transferIndex; i++)
                if (ordered[i].Kind is not (AutomationNodeKind.KindFilter or AutomationNodeKind.NameContains))
                    errors.Add("传输节点之前只允许放置类型或名称筛选。");
            var tail = ordered.Skip(transferIndex + 1).ToArray();
            if (tail.Length > 1 || (tail.Length == 1 && tail[0].Kind != AutomationNodeKind.SystemNotification))
                errors.Add("传输节点之后只允许一个终点“系统通知”节点。");
        }
        if (notifications.Length > 1)
            errors.Add("无条件工作流最多只能包含一个通知节点。");
    }

    private static void ValidateConditionalGraph(
        IReadOnlyList<AutomationNodeDefinition> nodes,
        IReadOnlyDictionary<string, AutomationNodeDefinition> nodeById,
        IReadOnlyDictionary<string, List<AutomationConnectionDefinition>> outgoing,
        IReadOnlyDictionary<string, List<AutomationConnectionDefinition>> incoming,
        AutomationNodeDefinition[] sources,
        AutomationNodeDefinition[] transfers,
        AutomationNodeDefinition[] conditions,
        AutomationNodeDefinition[] notifications,
        List<string> errors)
    {
        if (sources.Length != 1 || transfers.Length != 1 || conditions.Length != 1 || notifications.Length > 1)
            return;

        var source = sources[0];
        var condition = conditions[0];
        var transfer = transfers[0];
        var notification = notifications.SingleOrDefault();
        var prefix = new List<AutomationNodeDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = source;
        while (current.Id != condition.Id && seen.Add(current.Id))
        {
            prefix.Add(current);
            var edges = outgoing[current.Id];
            if (edges.Count != 1 || !nodeById.TryGetValue(edges[0].TargetNodeId, out var next))
                break;
            current = next;
        }
        if (prefix.Count == 0 || prefix[0].Kind != AutomationNodeKind.CurrentFolder ||
            prefix.Skip(1).Any(node => node.Kind is not (AutomationNodeKind.KindFilter or AutomationNodeKind.NameContains)) ||
            current.Id != condition.Id)
            errors.Add("条件节点之前只允许“当前文件夹”与顺序串接的筛选节点。");

        var conditionEdges = outgoing[condition.Id];
        var matchedEdge = conditionEdges.FirstOrDefault(edge => edge.SourcePortId == AutomationPortIds.Matched);
        var unmatchedEdge = conditionEdges.FirstOrDefault(edge => edge.SourcePortId == AutomationPortIds.Unmatched);
        if (matchedEdge is null || matchedEdge.TargetNodeId != transfer.Id)
            errors.Add("条件节点的“达到阈值”分支必须连接到唯一传输节点。");
        if (conditionEdges.Count != (unmatchedEdge is null ? 1 : 2))
            errors.Add("条件节点只允许“达到阈值”和“未达到阈值”两条分支。");
        if (unmatchedEdge is not null && (notification is null || unmatchedEdge.TargetNodeId != notification.Id))
            errors.Add("“未达到阈值”分支只允许连接到系统通知，或留空作为安全终止路径。");

        var transferEdges = outgoing[transfer.Id];
        if (notification is null)
        {
            if (transferEdges.Count != 0)
                errors.Add("没有通知节点时，传输节点必须是终点。");
        }
        else if (transferEdges.Count != 1 || transferEdges[0].TargetNodeId != notification.Id)
            errors.Add("传输节点必须连接到系统通知终点。");

        if (notification is not null && outgoing[notification.Id].Count != 0)
            errors.Add("系统通知是终点，不能再向后连接。");

        IEnumerable<string> notificationIds = notification is null
            ? Enumerable.Empty<string>()
            : new[] { notification.Id };
        var expectedIds = prefix.Select(node => node.Id).Append(condition.Id).Append(transfer.Id)
            .Concat(notificationIds).ToHashSet(StringComparer.Ordinal);
        if (expectedIds.Count != nodeById.Count || nodeById.Keys.Any(id => !expectedIds.Contains(id)))
            errors.Add("条件图仅支持：来源 → 筛选 → 条件 →（传输 / 安全终止）→ 可选通知；不接受额外节点或嵌套分支。");

        if (notification is not null)
        {
            var notifyIncoming = incoming[notification.Id];
            if (!notifyIncoming.Any(edge => edge.SourceNodeId == transfer.Id) ||
                (unmatchedEdge is not null && !notifyIncoming.Any(edge => edge.SourceNodeId == condition.Id && edge.SourcePortId == AutomationPortIds.Unmatched)))
                errors.Add("系统通知的合流输入与条件分支不一致。");
        }
    }

    private static IReadOnlyList<AutomationNodeDefinition> TopologicalOrder(
        IReadOnlyDictionary<string, AutomationNodeDefinition> nodes,
        IReadOnlyDictionary<string, List<AutomationConnectionDefinition>> outgoing,
        IReadOnlyDictionary<string, List<AutomationConnectionDefinition>> incoming,
        List<string> errors)
    {
        var inDegree = incoming.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
        var queue = new Queue<string>(nodes.Keys.Where(id => inDegree[id] == 0));
        var ordered = new List<AutomationNodeDefinition>(nodes.Count);
        while (queue.TryDequeue(out var id))
        {
            ordered.Add(nodes[id]);
            foreach (var edge in outgoing[id])
            {
                inDegree[edge.TargetNodeId]--;
                if (inDegree[edge.TargetNodeId] == 0)
                    queue.Enqueue(edge.TargetNodeId);
            }
        }
        if (ordered.Count != nodes.Count)
        {
            errors.Add("工作流包含循环连线；自动化图必须是有界 DAG。");
            return [];
        }
        return ordered;
    }

    internal static void ValidateNotificationText(
        string? value,
        string label,
        int maxLength,
        bool requireNonEmpty,
        List<string> errors)
    {
        var text = value ?? string.Empty;
        if (requireNonEmpty && string.IsNullOrWhiteSpace(text))
            errors.Add($"{label}不能为空。");
        if (text.Length > maxLength)
            errors.Add($"{label}过长（上限 {maxLength} 字符）。");

        var offset = 0;
        while (offset < text.Length)
        {
            var open = text.IndexOf('{', offset);
            if (open < 0)
                break;
            var close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                errors.Add($"{label}包含未闭合的占位符。");
                break;
            }
            var placeholder = text[(open + 1)..close].Trim();
            if (!placeholder.Equals("count", StringComparison.OrdinalIgnoreCase) &&
                !placeholder.Equals("succeeded", StringComparison.OrdinalIgnoreCase) &&
                !placeholder.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                !placeholder.Equals("destination", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label}包含未知占位符：{{{placeholder}}}。仅支持 {{count}}、{{succeeded}}、{{failed}}、{{destination}}。");
            offset = close + 1;
        }
    }
}

/// <summary>
/// 自动化执行器。来源固定为运行时的活动浏览器文件夹；仅枚举一层文件、只在有匹配时
/// 创建目标夹，并以 KeepBoth 复用既有安全传输管线，节点编辑器不得绕过任务中心或覆盖文件。
/// </summary>
public sealed class AutomationWorkflowExecutor
{
    public const int MaxTopLevelFiles = 100_000;
    public const long MaxTotalTransferBytes = 20L * 1024 * 1024 * 1024;

    private readonly IFileSystemService _fileSystem;
    private readonly IFileOperationService _operations;
    private readonly ISystemNotificationService _notifications;

    public AutomationWorkflowExecutor(
        IFileSystemService fileSystem,
        IFileOperationService operations,
        ISystemNotificationService notifications)
    {
        _fileSystem = fileSystem;
        _operations = operations;
        _notifications = notifications;
    }

    public async Task<AutomationExecutionResult> ExecuteAsync(
        AutomationWorkflowDefinition workflow,
        string sourceFolder,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var validation = AutomationWorkflowValidator.Validate(workflow);
        if (!validation.IsValid)
        {
            return Failed(
                validation.Errors.Select(error => new OperationError("自动化工作流", error)).ToArray(),
                [],
                null,
                validation.Errors);
        }

        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            return Failed(
                [new OperationError("当前文件夹", "尚未打开可运行自动化的文件夹。")],
                [],
                null,
                []);
        }
        if (FileLocator.IsStorageUri(sourceFolder))
        {
            return Failed(
                [new OperationError("当前文件夹", "自动化第一版仅支持本地文件夹，暂不支持 Android 存储授权目录（SAF）。")],
                [],
                null,
                []);
        }
        if (!Path.IsPathFullyQualified(sourceFolder))
        {
            return Failed(
                [new OperationError("当前文件夹", "自动化来源必须是本地绝对路径。")],
                [],
                null,
                []);
        }

        var files = new List<FileItem>();
        var enumeratedItems = 0;
        try
        {
            await foreach (var item in _fileSystem.ListAsync(sourceFolder, false, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++enumeratedItems > MaxTopLevelFiles)
                {
                    return Failed(
                        [new OperationError("当前文件夹", $"顶层条目超过安全上限 {MaxTopLevelFiles}，已停止且未传输。", sourceFolder)],
                        [],
                        null,
                        []);
                }
                if (item.IsFolder)
                    continue;
                files.Add(item);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AutomationExecutionResult(
                new OperationOutcome(0, [], [], Cancelled: true), [], null, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Failed(
                [new OperationError(Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), $"枚举当前文件夹失败：{ex.Message}", sourceFolder)],
                [],
                null,
                []);
        }

        var ordered = validation.OrderedNodes;
        var transferNode = ordered.First(node => node.Kind == AutomationNodeKind.TransferToSubfolder);
        var conditionNode = ordered.FirstOrDefault(node => node.Kind == AutomationNodeKind.MatchCountCondition);
        var notificationNode = ordered.FirstOrDefault(node => node.Kind == AutomationNodeKind.SystemNotification);
        var filterNodes = ordered
            .TakeWhile(node => node.Kind is not (AutomationNodeKind.MatchCountCondition or AutomationNodeKind.TransferToSubfolder))
            .Where(node => node.Kind is AutomationNodeKind.KindFilter or AutomationNodeKind.NameContains)
            .ToArray();
        var matched = files.Where(file => filterNodes.All(node => Matches(file, node))).ToArray();

        if (conditionNode is not null && matched.Length < conditionNode.MinimumMatchCount)
        {
            progress?.Report(new OperationProgress(1, $"匹配数未达到 {conditionNode.MinimumMatchCount}，走未达标分支", 0, 0));
            var skippedOutcome = new OperationOutcome(0, [], []);
            var hasNotificationEdge = workflow.Connections.Any(connection =>
                connection.SourceNodeId == conditionNode.Id &&
                connection.SourcePortId == AutomationPortIds.Unmatched);
            var skippedNotification = hasNotificationEdge
                ? await TryNotifyAsync(notificationNode, skippedOutcome, matched.Length, null, cancellationToken).ConfigureAwait(false)
                : null;
            return new AutomationExecutionResult(skippedOutcome, matched, null, skippedNotification, []);
        }

        if (matched.Length == 0)
        {
            progress?.Report(new OperationProgress(1, "没有匹配文件", 0, 0));
            var emptyOutcome = new OperationOutcome(0, [], []);
            var emptyNotification = await TryNotifyAsync(
                notificationNode, emptyOutcome, matched.Length, null, cancellationToken).ConfigureAwait(false);
            return new AutomationExecutionResult(emptyOutcome, matched, null, emptyNotification, []);
        }

        long estimatedBytes = 0;
        foreach (var file in matched)
        {
            if (file.Length is not { } fileLength || fileLength < 0)
            {
                return Failed(
                    [new OperationError(file.Name, "无法获取文件大小，自动化为避免超出传输预算已停止；未创建目标文件夹。", file.FullPath)],
                    matched,
                    null,
                    []);
            }
            if (fileLength > MaxTotalTransferBytes - estimatedBytes)
            {
                return Failed(
                    [new OperationError("自动化预算", $"匹配文件总量超过安全上限 {MaxTotalTransferBytes / (1024 * 1024 * 1024)} GiB，已停止且未创建目标文件夹。")],
                    matched,
                    null,
                    []);
            }
            estimatedBytes += fileLength;
        }

        string destinationPath;
        try
        {
            destinationPath = await _fileSystem.CreateFolderAsync(
                sourceFolder,
                transferNode.DestinationSubfolder.Trim(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AutomationExecutionResult(
                new OperationOutcome(0, [], [], Cancelled: true), matched, null, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Failed(
                [new OperationError(transferNode.DestinationSubfolder.Trim(), $"创建目标子文件夹失败：{ex.Message}", sourceFolder)],
                matched,
                null,
                []);
        }

        progress?.Report(new OperationProgress(0, "正在传输匹配文件", 0, matched.Length));
        OperationOutcome outcome;
        try
        {
            var sources = matched.Select(file => file.FullPath).ToArray();
            outcome = transferNode.TransferMode == AutomationTransferMode.Move
                ? await _operations.MoveAsync(sources, destinationPath, ConflictPolicy.KeepBoth, progress, cancellationToken).ConfigureAwait(false)
                : await _operations.CopyAsync(sources, destinationPath, ConflictPolicy.KeepBoth, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AutomationExecutionResult(
                new OperationOutcome(0, [], [], Cancelled: true), matched, destinationPath, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Failed(
                [new OperationError("传输文件", ex.Message, sourceFolder)],
                matched,
                destinationPath,
                []);
        }

        if (outcome.Cancelled)
            return new AutomationExecutionResult(outcome, matched, destinationPath, null, []);

        var notification = await TryNotifyAsync(
            notificationNode, outcome, matched.Length, destinationPath, cancellationToken).ConfigureAwait(false);
        return new AutomationExecutionResult(outcome, matched, destinationPath, notification, []);
    }

    public static string FormatNotificationTemplate(
        string? template,
        int count,
        int succeeded,
        int failed,
        string? destination)
    {
        var text = template ?? string.Empty;
        return text
            .Replace("{count}", count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{succeeded}", succeeded.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{failed}", failed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{destination}", destination ?? "未创建目标文件夹", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(FileItem file, AutomationNodeDefinition node) => node.Kind switch
    {
        AutomationNodeKind.KindFilter => node.FilterKind is null || file.Kind == node.FilterKind.Value,
        AutomationNodeKind.NameContains => string.IsNullOrWhiteSpace(node.NameQuery) ||
            file.Name.Contains(node.NameQuery.Trim(), StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private async Task<SystemNotificationResult?> TryNotifyAsync(
        AutomationNodeDefinition? node,
        OperationOutcome outcome,
        int matchedCount,
        string? destinationPath,
        CancellationToken cancellationToken)
    {
        if (node is null || outcome.Cancelled)
            return null;

        var failed = Math.Max(0, outcome.Errors.Count + outcome.SkippedCount);
        var title = FormatNotificationTemplate(
            node.NotificationTitle, matchedCount, outcome.SucceededCount, failed, destinationPath);
        var body = FormatNotificationTemplate(
            node.NotificationBody, matchedCount, outcome.SucceededCount, failed, destinationPath);
        try
        {
            return await _notifications.ShowAsync(title, body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SystemNotificationResult.Failed(ex.Message);
        }
    }

    private static AutomationExecutionResult Failed(
        IReadOnlyList<OperationError> errors,
        IReadOnlyList<FileItem> matchedFiles,
        string? destinationPath,
        IReadOnlyList<string> validationErrors) => new(
        new OperationOutcome(0, errors, []),
        matchedFiles,
        destinationPath,
        null,
        validationErrors);
}
