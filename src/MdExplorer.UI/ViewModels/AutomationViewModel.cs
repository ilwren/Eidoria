using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using NodifyM.Avalonia.ViewModelBase;

namespace MdExplorer.UI.ViewModels;

/// <summary>带所属节点回指的连接器；NodifyM 上游连接器本身只保存锚点与流向。</summary>
public sealed class AutomationConnectorViewModel : ConnectorViewModelBase
{
    public AutomationConnectorViewModel(AutomationNodeViewModel owner, ConnectorFlow flow, string portId = AutomationPortIds.Default)
    {
        Owner = owner;
        Flow = flow;
        PortId = portId;
        Title = flow == ConnectorFlow.Input ? "输入" : portId switch
        {
            AutomationPortIds.Matched => "达到阈值",
            AutomationPortIds.Unmatched => "未达阈值",
            _ => "输出",
        };
        CanConnect = true;
    }

    public AutomationNodeViewModel Owner { get; }
    public string PortId { get; }
}

/// <summary>自动化连接。禁止上游的 Knot 分裂语义，避免画布生成协议未定义的中继节点。</summary>
public sealed class AutomationConnectionViewModel : ConnectionViewModelBase
{
    private readonly AutomationViewModel _editor;

    public AutomationConnectionViewModel(
        AutomationViewModel editor,
        ConnectorViewModelBase source,
        ConnectorViewModelBase target)
        : base(editor, source, target)
    {
        _editor = editor;
    }

    public override void DisconnectConnection(ConnectionViewModelBase connection) =>
        _editor.Disconnect(connection);

    public override void SplitConnection(Point location)
    {
        // Intentionally disabled for the linear v1 workflow.
    }
}

/// <summary>
/// 节点画布中的一个自动化步骤。它直接写回可持久化定义；编辑器 VM 负责连线、整体验证
/// 与防抖保存。节点脚注始终给出当前参数摘要，避免用户必须逐个点开才知道流水线意图。
/// </summary>
public sealed partial class AutomationNodeViewModel : NodeViewModelBase
{
    private readonly Action _changed;
    private bool _initializing = true;

    public AutomationNodeViewModel(AutomationNodeDefinition definition, Action changed)
    {
        Definition = definition;
        _changed = changed;
        Title = DisplayNameFor(definition.Kind);
        Location = new Point(
            double.IsFinite(definition.X) ? definition.X : 24,
            double.IsFinite(definition.Y) ? definition.Y : 128);

        if (definition.Kind != AutomationNodeKind.CurrentFolder)
            Input.Add(new AutomationConnectorViewModel(this, ConnectorViewModelBase.ConnectorFlow.Input));
        if (definition.Kind == AutomationNodeKind.MatchCountCondition)
        {
            Output.Add(new AutomationConnectorViewModel(this, ConnectorViewModelBase.ConnectorFlow.Output, AutomationPortIds.Matched));
            Output.Add(new AutomationConnectorViewModel(this, ConnectorViewModelBase.ConnectorFlow.Output, AutomationPortIds.Unmatched));
        }
        else if (definition.Kind != AutomationNodeKind.SystemNotification)
        {
            Output.Add(new AutomationConnectorViewModel(this, ConnectorViewModelBase.ConnectorFlow.Output));
        }

        RefreshFooter();
        _initializing = false;
    }

    public AutomationNodeDefinition Definition { get; }
    public string Id => Definition.Id;
    public AutomationNodeKind Kind => Definition.Kind;

    public AutomationConnectorViewModel? InputConnector =>
        Input.OfType<AutomationConnectorViewModel>().FirstOrDefault();

    public AutomationConnectorViewModel? OutputConnector =>
        Output.OfType<AutomationConnectorViewModel>().FirstOrDefault(connector => connector.PortId == AutomationPortIds.Default)
        ?? Output.OfType<AutomationConnectorViewModel>().FirstOrDefault();

    public AutomationConnectorViewModel? GetOutputConnector(string portId) =>
        Output.OfType<AutomationConnectorViewModel>().FirstOrDefault(connector => connector.PortId == portId);

    public bool IsCurrentFolder => Kind == AutomationNodeKind.CurrentFolder;
    public bool IsKindFilter => Kind == AutomationNodeKind.KindFilter;
    public bool IsNameFilter => Kind == AutomationNodeKind.NameContains;
    public bool IsMatchCountCondition => Kind == AutomationNodeKind.MatchCountCondition;
    public bool IsTransfer => Kind == AutomationNodeKind.TransferToSubfolder;
    public bool IsNotification => Kind == AutomationNodeKind.SystemNotification;
    public bool CanDelete => Kind is AutomationNodeKind.KindFilter or AutomationNodeKind.NameContains or
        AutomationNodeKind.MatchCountCondition or AutomationNodeKind.SystemNotification;

    public FileKind? FilterKind
    {
        get => Definition.FilterKind;
        set
        {
            if (Definition.FilterKind == value)
                return;
            Definition.FilterKind = value;
            OnPropertyChanged();
            RefreshFooter();
            NotifyFilterSelectionChanged();
            NotifyChanged();
        }
    }

    public bool IsFilterAny => FilterKind is null;
    public bool IsFilterImage => FilterKind == FileKind.Image;
    public bool IsFilterVideo => FilterKind == FileKind.Video;
    public bool IsFilterAudio => FilterKind == FileKind.Audio;
    public bool IsFilterDocument => FilterKind == FileKind.Document;
    public bool IsFilterArchive => FilterKind == FileKind.Archive;
    public bool IsFilterCode => FilterKind == FileKind.Code;
    public bool IsFilterText => FilterKind == FileKind.Text;
    public bool IsFilterData => FilterKind == FileKind.Data;
    public bool IsFilterFont => FilterKind == FileKind.Font;
    public bool IsFilterExecutable => FilterKind == FileKind.Executable;
    public bool IsFilterUnknown => FilterKind == FileKind.Unknown;

    public string NameQuery
    {
        get => Definition.NameQuery ?? string.Empty;
        set => SetDefinitionText(
            value,
            Definition.NameQuery ?? string.Empty,
            static (definition, text) => definition.NameQuery = text);
    }

    public int MinimumMatchCount
    {
        get => Definition.MinimumMatchCount;
        set
        {
            if (Definition.MinimumMatchCount == value)
                return;
            Definition.MinimumMatchCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinimumMatchCountText));
            RefreshFooter();
            NotifyChanged();
        }
    }

    public string MinimumMatchCountText
    {
        get => MinimumMatchCount.ToString(CultureInfo.InvariantCulture);
        set => MinimumMatchCount = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    public string DestinationSubfolder
    {
        get => Definition.DestinationSubfolder ?? string.Empty;
        set => SetDefinitionText(
            value,
            Definition.DestinationSubfolder ?? string.Empty,
            static (definition, text) => definition.DestinationSubfolder = text);
    }

    public AutomationTransferMode TransferMode
    {
        get => Definition.TransferMode;
        set
        {
            if (Definition.TransferMode == value)
                return;
            Definition.TransferMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCopyMode));
            OnPropertyChanged(nameof(IsMoveMode));
            RefreshFooter();
            NotifyChanged();
        }
    }

    public bool IsCopyMode => TransferMode == AutomationTransferMode.Copy;
    public bool IsMoveMode => TransferMode == AutomationTransferMode.Move;

    public string NotificationTitle
    {
        get => Definition.NotificationTitle ?? string.Empty;
        set => SetDefinitionText(
            value,
            Definition.NotificationTitle ?? string.Empty,
            static (definition, text) => definition.NotificationTitle = text);
    }

    public string NotificationBody
    {
        get => Definition.NotificationBody ?? string.Empty;
        set => SetDefinitionText(
            value,
            Definition.NotificationBody ?? string.Empty,
            static (definition, text) => definition.NotificationBody = text);
    }

    public void SyncLocationToDefinition()
    {
        Definition.X = Location.X;
        Definition.Y = Location.Y;
    }

    public void NotifyLocationChanged()
    {
        SyncLocationToDefinition();
        NotifyChanged();
    }

    public static string DisplayNameFor(AutomationNodeKind kind) => kind switch
    {
        AutomationNodeKind.CurrentFolder => "当前文件夹",
        AutomationNodeKind.KindFilter => "类型筛选",
        AutomationNodeKind.NameContains => "名称筛选",
        AutomationNodeKind.MatchCountCondition => "匹配数量条件",
        AutomationNodeKind.TransferToSubfolder => "传输到子文件夹",
        AutomationNodeKind.SystemNotification => "系统通知",
        _ => "未知节点",
    };

    public static string FileKindName(FileKind? kind) => kind switch
    {
        null => "任意文件",
        FileKind.Folder => "文件夹",
        FileKind.Image => "图片",
        FileKind.Video => "视频",
        FileKind.Audio => "音频",
        FileKind.Document => "文档",
        FileKind.Archive => "压缩包",
        FileKind.Code => "代码",
        FileKind.Text => "文本",
        FileKind.Data => "数据",
        FileKind.Font => "字体",
        FileKind.Executable => "可执行文件",
        FileKind.Unknown => "其他",
        _ => "未知类型",
    };

    private void SetDefinitionText(
        string? value,
        string current,
        Action<AutomationNodeDefinition, string> setter,
        [CallerMemberName] string propertyName = "")
    {
        var text = value ?? string.Empty;
        if (string.Equals(current, text, StringComparison.Ordinal))
            return;
        setter(Definition, text);
        OnPropertyChanged(propertyName);
        RefreshFooter();
        NotifyChanged();
    }

    private void RefreshFooter()
    {
        Footer = Kind switch
        {
            AutomationNodeKind.CurrentFolder => "仅处理顶层文件",
            AutomationNodeKind.KindFilter => FileKindName(FilterKind),
            AutomationNodeKind.NameContains => string.IsNullOrWhiteSpace(NameQuery)
                ? "名称不限"
                : $"名称包含：{NameQuery.Trim()}",
            AutomationNodeKind.MatchCountCondition => $"匹配数 ≥ {MinimumMatchCount} 继续",
            AutomationNodeKind.TransferToSubfolder =>
                $"{(TransferMode == AutomationTransferMode.Move ? "移动" : "复制")} → {DestinationSubfolder.Trim()}",
            AutomationNodeKind.SystemNotification => string.IsNullOrWhiteSpace(NotificationTitle)
                ? "未填写标题"
                : NotificationTitle.Trim(),
            _ => "请删除此节点",
        };
    }

    private void NotifyFilterSelectionChanged()
    {
        OnPropertyChanged(nameof(IsFilterAny));
        OnPropertyChanged(nameof(IsFilterImage));
        OnPropertyChanged(nameof(IsFilterVideo));
        OnPropertyChanged(nameof(IsFilterAudio));
        OnPropertyChanged(nameof(IsFilterDocument));
        OnPropertyChanged(nameof(IsFilterArchive));
        OnPropertyChanged(nameof(IsFilterCode));
        OnPropertyChanged(nameof(IsFilterText));
        OnPropertyChanged(nameof(IsFilterData));
        OnPropertyChanged(nameof(IsFilterFont));
        OnPropertyChanged(nameof(IsFilterExecutable));
        OnPropertyChanged(nameof(IsFilterUnknown));
    }

    private void NotifyChanged()
    {
        if (!_initializing)
            _changed();
    }
}

/// <summary>
/// 自动化页面与 NodifyM 的结合点。画布只维护可视化图，真正的结构合法性、来源安全与
/// 文件传输始终由 Application 层校验器/执行器裁决；运行也必须经过 Shell 的任务中心。
/// </summary>
public sealed partial class AutomationViewModel : NodifyEditorViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly AutomationWorkflowExecutor _executor;
    private readonly IDialogs _dialogs;
    private readonly Func<string?> _currentFolderProvider;
    private readonly Func<Task> _refreshBrowsers;
    private readonly Action<string> _notify;
    private bool _buildingGraph;
    private CancellationTokenSource? _persistCts;

    public AutomationViewModel(
        AppHost.AppServices services,
        IDialogs dialogs,
        Func<string?> currentFolderProvider,
        Func<Task> refreshBrowsers,
        Action<string> notify)
    {
        _settingsStore = services.SettingsStore;
        _executor = new AutomationWorkflowExecutor(
            services.FileSystem,
            services.Operations,
            services.SystemNotifications);
        _dialogs = dialogs;
        _currentFolderProvider = currentFolderProvider;
        _refreshBrowsers = refreshBrowsers;
        _notify = notify;

        _workflowName = _settingsStore.Settings.AutomationWorkflow.Name ?? "整理当前文件夹";
        Nodes.CollectionChanged += OnNodesChanged;
        Connections.CollectionChanged += OnConnectionsChanged;
        SelectedNodes.CollectionChanged += OnSelectedNodesChanged;
        RebuildGraph();
    }

    [ObservableProperty] private string _workflowName = "整理当前文件夹";
    [ObservableProperty] private AutomationNodeViewModel? _selectedNode;
    [ObservableProperty] private bool _isWorkflowValid;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _validationText = string.Empty;
    [ObservableProperty] private string _statusText = "拖动节点调整布局，从输出点拖到输入点连线。";
    [ObservableProperty] private string _currentFolderText = "尚未打开文件夹";
    [ObservableProperty] private string _lastResultText = "尚未运行；来源始终是主浏览器当前文件夹。";

    public bool HasSelectedNode => SelectedNode is not null;
    public bool IsCurrentFolderSelected => SelectedNode?.IsCurrentFolder == true;
    public bool IsKindFilterSelected => SelectedNode?.IsKindFilter == true;
    public bool IsNameFilterSelected => SelectedNode?.IsNameFilter == true;
    public bool IsMatchCountConditionSelected => SelectedNode?.IsMatchCountCondition == true;
    public bool IsTransferSelected => SelectedNode?.IsTransfer == true;
    public bool IsNotificationSelected => SelectedNode?.IsNotification == true;
    public bool CanDeleteSelectedNode => SelectedNode?.CanDelete == true;
    public bool CanAddMatchCountCondition => AutomationNodes.All(node => !node.IsMatchCountCondition);
    public bool CanAddNotificationNode => AutomationNodes.All(node => !node.IsNotification);
    public string SelectedNodeTitle => SelectedNode?.Title ?? "未选择节点";
    public string SelectedNodeDescription => SelectedNode?.Kind switch
    {
        AutomationNodeKind.CurrentFolder => "运行时读取主浏览器当前文件夹中的顶层文件，不递归、不处理文件夹。",
        AutomationNodeKind.KindFilter => "只保留选定类型；选“任意”时不按类型收窄。",
        AutomationNodeKind.NameContains => "只保留文件名包含指定文本的文件（不区分大小写）。",
        AutomationNodeKind.MatchCountCondition => "按前置筛选后的匹配数量分支：达到阈值时传输；未达到时安全终止，或连接到通知节点。",
        AutomationNodeKind.TransferToSubfolder => "在当前文件夹下创建一级子文件夹并传输；同名始终保留两份。",
        AutomationNodeKind.SystemNotification => "所连分支结束后把摘要发送到操作系统；通知失败不会改判文件结果。",
        _ => "点击画布中的节点以编辑参数。",
    };

    private IEnumerable<AutomationNodeViewModel> AutomationNodes =>
        Nodes.OfType<AutomationNodeViewModel>();

    public void RefreshRuntimeState()
    {
        var folder = _currentFolderProvider();
        CurrentFolderText = string.IsNullOrWhiteSpace(folder)
            ? "尚未打开文件夹"
            : FileLocator.IsStorageUri(folder)
                ? $"{folder}（SAF，第一版暂不支持运行）"
                : folder;
        RefreshValidation();
    }

    public override void Connect(ConnectorViewModelBase source, ConnectorViewModelBase target)
    {
        if (source is not AutomationConnectorViewModel sourceConnector ||
            target is not AutomationConnectorViewModel targetConnector)
        {
            StatusText = "只能连接自动化节点的输入/输出点。";
            return;
        }

        AutomationConnectorViewModel output;
        AutomationConnectorViewModel input;
        if (sourceConnector.Flow == ConnectorViewModelBase.ConnectorFlow.Output &&
            targetConnector.Flow == ConnectorViewModelBase.ConnectorFlow.Input)
        {
            output = sourceConnector;
            input = targetConnector;
        }
        else if (sourceConnector.Flow == ConnectorViewModelBase.ConnectorFlow.Input &&
                 targetConnector.Flow == ConnectorViewModelBase.ConnectorFlow.Output)
        {
            output = targetConnector;
            input = sourceConnector;
        }
        else
        {
            StatusText = "请从一个节点的输出点连接到另一个节点的输入点。";
            return;
        }

        if (ReferenceEquals(output.Owner, input.Owner))
        {
            StatusText = "节点不能连接到自身。";
            return;
        }
        if (output.Owner.IsNotification)
        {
            StatusText = "系统通知是流水线终点，不能再向后连接。";
            return;
        }
        if (input.Owner.IsCurrentFolder)
        {
            StatusText = "当前文件夹是流水线起点，不能接收连线。";
            return;
        }
        if (Connections.Any(connection => ReferenceEquals(connection.Source, output)))
        {
            StatusText = $"“{output.Owner.Title}”的“{output.Title}”端口已有一条出线，请先断开原连线。";
            return;
        }
        if (input.Owner.IsNotification && !output.Owner.IsTransfer &&
            !(output.Owner.IsMatchCountCondition && output.PortId == AutomationPortIds.Unmatched))
        {
            StatusText = "系统通知只接收传输结果或条件的未达标分支。";
            return;
        }
        var targetConnections = Connections.Where(connection => ReferenceEquals(connection.Target, input)).ToArray();
        if (targetConnections.Length >= (input.Owner.IsNotification ? 2 : 1) ||
            (targetConnections.Length > 0 && !input.Owner.IsNotification))
        {
            StatusText = $"“{input.Owner.Title}”的入线已达安全上限。";
            return;
        }
        if (input.Owner.IsNotification && targetConnections.Length == 1)
        {
            var existingSource = targetConnections[0].Source as AutomationConnectorViewModel;
            var isValidMerge = existingSource is not null &&
                ((existingSource.Owner.IsTransfer && output.Owner.IsMatchCountCondition && output.PortId == AutomationPortIds.Unmatched) ||
                 (existingSource.Owner.IsMatchCountCondition && existingSource.PortId == AutomationPortIds.Unmatched && output.Owner.IsTransfer));
            if (!isValidMerge)
            {
                StatusText = "系统通知合流只允许传输节点与条件的未达标分支。";
                return;
            }
        }
        if (WouldCreateCycle(output.Owner, input.Owner))
        {
            StatusText = "这条连线会形成循环，已拒绝。";
            return;
        }

        Connections.Add(new AutomationConnectionViewModel(this, output, input));
        StatusText = $"已连接：{output.Owner.Title} → {input.Owner.Title}。";
    }

    public override void DisconnectConnector(ConnectorViewModelBase connector)
    {
        foreach (var connection in Connections
                     .Where(connection => ReferenceEquals(connection.Source, connector) ||
                                          ReferenceEquals(connection.Target, connector))
                     .ToArray())
        {
            Connections.Remove(connection);
        }
        RefreshConnectorStates();
    }

    public void Disconnect(ConnectionViewModelBase connection)
    {
        if (Connections.Remove(connection))
            StatusText = "已断开连线。";
        RefreshConnectorStates();
    }

    [RelayCommand]
    private void AddKindFilterNode() => AddNode(AutomationNodeKind.KindFilter);

    [RelayCommand]
    private void AddNameFilterNode() => AddNode(AutomationNodeKind.NameContains);

    [RelayCommand]
    private void AddMatchCountConditionNode()
    {
        if (!CanAddMatchCountCondition)
        {
            StatusText = "当前只支持一个匹配数量条件节点。";
            return;
        }
        AddNode(AutomationNodeKind.MatchCountCondition);
    }

    [RelayCommand]
    private void AddNotificationNode()
    {
        if (!CanAddNotificationNode)
        {
            StatusText = "工作流已有一个系统通知终点。";
            return;
        }
        AddNode(AutomationNodeKind.SystemNotification);
    }

    [RelayCommand]
    private void DeleteSelectedNode()
    {
        if (SelectedNode is not { } node)
            return;
        if (!node.CanDelete)
        {
            StatusText = "“当前文件夹”与“传输到子文件夹”是必备节点，不能删除。";
            return;
        }

        foreach (var connection in Connections
                     .Where(connection =>
                         (connection.Source is AutomationConnectorViewModel source && ReferenceEquals(source.Owner, node)) ||
                         (connection.Target is AutomationConnectorViewModel target && ReferenceEquals(target.Owner, node)))
                     .ToArray())
        {
            Connections.Remove(connection);
        }
        Nodes.Remove(node);
        SelectNode(AutomationNodes.FirstOrDefault(candidate => candidate.IsTransfer) ??
                   AutomationNodes.FirstOrDefault());
        StatusText = $"已删除“{node.Title}”节点。";
    }

    [RelayCommand]
    private void DisconnectSelectedNode()
    {
        if (SelectedNode is not { } node)
            return;
        foreach (var connection in Connections
                     .Where(connection =>
                         (connection.Source is AutomationConnectorViewModel source && ReferenceEquals(source.Owner, node)) ||
                         (connection.Target is AutomationConnectorViewModel target && ReferenceEquals(target.Owner, node)))
                     .ToArray())
        {
            Connections.Remove(connection);
        }
        StatusText = $"已断开“{node.Title}”的全部连线。";
    }

    [RelayCommand]
    private void AutoLayout()
    {
        var source = AutomationNodes.FirstOrDefault(node => node.IsCurrentFolder);
        var filters = AutomationNodes
            .Where(node => node.Kind is AutomationNodeKind.KindFilter or AutomationNodeKind.NameContains)
            .ToArray();
        var condition = AutomationNodes.FirstOrDefault(node => node.IsMatchCountCondition);
        var transfer = AutomationNodes.FirstOrDefault(node => node.IsTransfer);
        var notification = AutomationNodes.FirstOrDefault(node => node.IsNotification);
        var ordered = new List<AutomationNodeViewModel>();
        if (source is not null) ordered.Add(source);
        ordered.AddRange(filters);
        if (condition is not null) ordered.Add(condition);
        if (transfer is not null) ordered.Add(transfer);
        if (notification is not null) ordered.Add(notification);
        ordered.AddRange(AutomationNodes.Where(node => !ordered.Contains(node)));

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Location = new Point(24 + i * 226, 128);
            ordered[i].NotifyLocationChanged();
        }
        StatusText = "已按来源、筛选、条件、传输与通知的常见顺序整理节点；可再拖动微调分支布局。";
    }

    [RelayCommand]
    private async Task LoadExampleAsync(string? exampleId)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(exampleId))
            return;

        AutomationWorkflowDefinition example;
        try
        {
            example = AutomationWorkflowDefinition.CreateExample(exampleId);
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText = "未知的工作流示例。";
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "载入节点示例？",
                $"当前画布「{WorkflowName}」将被示例「{example.Name}」替换；文件不会立即改动，只有点击“运行”后才会先进入计划确认。",
                "载入示例").ConfigureAwait(true))
            return;

        _buildingGraph = true;
        try
        {
            _settingsStore.Settings.AutomationWorkflow = example;
            WorkflowName = example.Name;
        }
        finally
        {
            _buildingGraph = false;
        }
        RebuildGraph();
        await PersistNowAsync().ConfigureAwait(true);
        StatusText = $"已载入「{example.Name}」示例；先检查节点与连线，再运行。";
        LastResultText = "尚未运行；示例只保存计划，不会自动操作文件。";
    }

    [RelayCommand]
    private async Task ResetWorkflowAsync()
    {
        _buildingGraph = true;
        try
        {
            _settingsStore.Settings.AutomationWorkflow = AutomationWorkflowDefinition.CreateDefault();
            WorkflowName = _settingsStore.Settings.AutomationWorkflow.Name;
        }
        finally
        {
            _buildingGraph = false;
        }
        RebuildGraph();
        await PersistNowAsync().ConfigureAwait(true);
        StatusText = "已恢复示例：当前文件夹中的图片复制到“图片整理”，然后系统通知。";
        LastResultText = "尚未运行；来源始终是主浏览器当前文件夹。";
    }

    [RelayCommand]
    private void SetSelectedFilterKind(string? value)
    {
        if (SelectedNode is not { IsKindFilter: true } node)
            return;
        node.FilterKind = string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase)
            ? null
            : Enum.TryParse<FileKind>(value, ignoreCase: true, out var kind)
                ? kind
                : node.FilterKind;
    }

    [RelayCommand]
    private void SetSelectedTransferMode(string? value)
    {
        if (SelectedNode is not { IsTransfer: true } node)
            return;
        if (Enum.TryParse<AutomationTransferMode>(value, ignoreCase: true, out var mode))
            node.TransferMode = mode;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning)
            return;

        await PersistNowAsync().ConfigureAwait(true);
        var definition = CloneDefinition(BuildDefinition());
        var validation = AutomationWorkflowValidator.Validate(definition);
        if (!validation.IsValid)
        {
            IsWorkflowValid = false;
            ValidationText = string.Join("；", validation.Errors);
            LastResultText = "未运行：请先修复下方图结构或节点参数问题。";
            _notify("自动化流程无效，未运行");
            return;
        }

        var sourceFolder = _currentFolderProvider();
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            LastResultText = "未运行：主浏览器尚未打开文件夹。";
            _notify("请先在主浏览器打开文件夹");
            return;
        }

        RefreshRuntimeState();
        IsRunning = true;
        LastResultText = "正在通过任务中心运行…";
        try
        {
            AutomationExecutionResult? result = null;
            var outcome = await _dialogs.RunOperationAsync(
                $"自动化 · {definition.Name.Trim()}",
                async (progress, cancellationToken) =>
                {
                    result = await _executor.ExecuteAsync(
                        definition, sourceFolder, progress, cancellationToken).ConfigureAwait(true);
                    return result.Operation;
                },
                retryFailed: null,
                suppressSystemNotification: definition.Nodes.Any(node =>
                    node.Kind == AutomationNodeKind.SystemNotification)).ConfigureAwait(true);

            if (result is not null)
            {
                var notificationText = result.Notification is null
                    ? "未配置流程内通知"
                    : result.Notification.Success
                        ? "系统通知已提交"
                        : $"系统通知未发送：{result.Notification.Message}";
                LastResultText = outcome.Cancelled
                    ? "运行已取消。"
                    : result.Operation.Success && result.DestinationPath is null &&
                      definition.Nodes.Any(node => node.Kind == AutomationNodeKind.MatchCountCondition) && result.MatchedFiles.Count > 0
                        ? $"未达到匹配数量阈值：筛选匹配 {result.MatchedFiles.Count}，未创建目标子文件夹；{notificationText}。"
                        : result.MatchedFiles.Count == 0
                            ? "运行完成：当前文件夹顶层没有匹配文件，未创建目标子文件夹。"
                            : $"运行完成：匹配 {result.MatchedFiles.Count}，成功 {outcome.SucceededCount}，失败 {outcome.Errors.Count}，跳过 {outcome.SkippedCount}；目标：{result.DestinationPath ?? "—"}；{notificationText}。";
            }
            else
            {
                LastResultText = outcome.Cancelled
                    ? "运行已取消。"
                    : $"运行完成：成功 {outcome.SucceededCount}，失败 {outcome.Errors.Count}。";
            }

            await _refreshBrowsers().ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            RefreshRuntimeState();
        }
    }

    partial void OnWorkflowNameChanged(string value)
    {
        if (_buildingGraph)
            return;
        _settingsStore.Settings.AutomationWorkflow.Name = value;
        RefreshValidation();
        SchedulePersist();
    }

    partial void OnSelectedNodeChanged(AutomationNodeViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(IsCurrentFolderSelected));
        OnPropertyChanged(nameof(IsKindFilterSelected));
        OnPropertyChanged(nameof(IsNameFilterSelected));
        OnPropertyChanged(nameof(IsMatchCountConditionSelected));
        OnPropertyChanged(nameof(IsTransferSelected));
        OnPropertyChanged(nameof(IsNotificationSelected));
        OnPropertyChanged(nameof(CanDeleteSelectedNode));
        OnPropertyChanged(nameof(SelectedNodeTitle));
        OnPropertyChanged(nameof(SelectedNodeDescription));
    }

    private void AddNode(AutomationNodeKind kind)
    {
        var index = AutomationNodes.Count();
        var definition = new AutomationNodeDefinition
        {
            Id = CreateUniqueNodeId(kind),
            Kind = kind,
            X = 48 + (index % 3) * 226,
            Y = 260 + (index / 3) * 36,
            FilterKind = kind == AutomationNodeKind.KindFilter ? FileKind.Image : null,
            DestinationSubfolder = "已整理",
            TransferMode = AutomationTransferMode.Copy,
            NotificationTitle = "自动化完成",
            NotificationBody = "已处理 {succeeded}/{count} 个文件到 {destination}。",
        };
        var node = CreateNodeViewModel(definition);
        Nodes.Add(node);
        SelectNode(node);
        StatusText = kind == AutomationNodeKind.MatchCountCondition
            ? "已加入条件节点：将筛选节点连接到条件输入；“达到阈值”必须连接唯一传输节点，“未达阈值”可留空或连接通知。"
            : $"已加入“{node.Title}”；请连接到工作流中，并检查图校验提示。";
    }

    private AutomationNodeViewModel CreateNodeViewModel(AutomationNodeDefinition definition)
    {
        var node = new AutomationNodeViewModel(definition, OnNodeDefinitionChanged);
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeViewModelBase.Location))
                node.NotifyLocationChanged();
        };
        return node;
    }

    private void RebuildGraph()
    {
        _buildingGraph = true;
        try
        {
            Connections.Clear();
            Nodes.Clear();
            SelectedNodes.Clear();

            var definition = _settingsStore.Settings.AutomationWorkflow;
            var nodeMap = new Dictionary<string, AutomationNodeViewModel>(StringComparer.Ordinal);
            foreach (var nodeDefinition in definition.Nodes ?? [])
            {
                if (nodeDefinition is null)
                    continue;
                nodeDefinition.Id = string.IsNullOrWhiteSpace(nodeDefinition.Id)
                    ? Guid.NewGuid().ToString("N")
                    : nodeDefinition.Id;
                var node = CreateNodeViewModel(nodeDefinition);
                Nodes.Add(node);
                nodeMap.TryAdd(node.Id, node);
            }

            foreach (var connection in definition.Connections ?? [])
            {
                if (connection is null ||
                    !nodeMap.TryGetValue(connection.SourceNodeId ?? string.Empty, out var source) ||
                    !nodeMap.TryGetValue(connection.TargetNodeId ?? string.Empty, out var target) ||
                    source.GetOutputConnector(string.IsNullOrWhiteSpace(connection.SourcePortId)
                        ? AutomationPortIds.Default
                        : connection.SourcePortId) is not { } output ||
                    target.InputConnector is not { } input)
                {
                    continue;
                }
                Connections.Add(new AutomationConnectionViewModel(this, output, input));
            }

            RefreshConnectorStates();
            SyncDefinition();
            RefreshValidation();
            var initial = AutomationNodes.FirstOrDefault(node => node.IsKindFilter) ??
                          AutomationNodes.FirstOrDefault();
            if (initial is not null)
            {
                SelectedNodes.Add(initial);
                SelectedNode = initial;
            }
            else
            {
                SelectedNode = null;
            }
            RefreshRuntimeState();
        }
        finally
        {
            _buildingGraph = false;
        }
    }

    private void SelectNode(AutomationNodeViewModel? node)
    {
        SelectedNodes.Clear();
        if (node is not null)
            SelectedNodes.Add(node);
        SelectedNode = node;
    }

    private AutomationWorkflowDefinition BuildDefinition()
    {
        SyncDefinition();
        return _settingsStore.Settings.AutomationWorkflow;
    }

    private void SyncDefinition()
    {
        var definition = _settingsStore.Settings.AutomationWorkflow;
        definition.Name = WorkflowName;
        definition.Nodes ??= [];
        definition.Connections ??= [];
        definition.Nodes.Clear();
        foreach (var node in AutomationNodes)
        {
            node.SyncLocationToDefinition();
            definition.Nodes.Add(node.Definition);
        }

        definition.Connections.Clear();
        foreach (var connection in Connections)
        {
            if (connection.Source is AutomationConnectorViewModel source &&
                connection.Target is AutomationConnectorViewModel target)
            {
                definition.Connections.Add(new AutomationConnectionDefinition
                {
                    SourceNodeId = source.Owner.Id,
                    SourcePortId = source.PortId,
                    TargetNodeId = target.Owner.Id,
                });
            }
        }
        OnPropertyChanged(nameof(CanAddNotificationNode));
        OnPropertyChanged(nameof(CanAddMatchCountCondition));
    }

    private static AutomationWorkflowDefinition CloneDefinition(AutomationWorkflowDefinition source) => new()
    {
        Name = source.Name,
        Nodes = source.Nodes.Select(static node => new AutomationNodeDefinition
        {
            Id = node.Id,
            Kind = node.Kind,
            X = node.X,
            Y = node.Y,
            FilterKind = node.FilterKind,
            NameQuery = node.NameQuery,
            MinimumMatchCount = node.MinimumMatchCount,
            DestinationSubfolder = node.DestinationSubfolder,
            TransferMode = node.TransferMode,
            NotificationTitle = node.NotificationTitle,
            NotificationBody = node.NotificationBody,
        }).ToList(),
        Connections = source.Connections.Select(static connection => new AutomationConnectionDefinition
        {
            SourceNodeId = connection.SourceNodeId,
            SourcePortId = connection.SourcePortId,
            TargetNodeId = connection.TargetNodeId,
        }).ToList(),
    };

    private bool WouldCreateCycle(AutomationNodeViewModel outputOwner, AutomationNodeViewModel inputOwner)
    {
        var pending = new Stack<AutomationNodeViewModel>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(inputOwner);
        while (pending.TryPop(out var current))
        {
            if (ReferenceEquals(current, outputOwner))
                return true;
            if (!visited.Add(current.Id))
                continue;
            foreach (var connection in Connections.Where(connection =>
                         connection.Source is AutomationConnectorViewModel source && ReferenceEquals(source.Owner, current)))
            {
                if (connection.Target is AutomationConnectorViewModel target)
                    pending.Push(target.Owner);
            }
        }
        return false;
    }

    private string CreateUniqueNodeId(AutomationNodeKind kind)
    {
        var prefix = kind switch
        {
            AutomationNodeKind.KindFilter => "kind-filter",
            AutomationNodeKind.NameContains => "name-filter",
            AutomationNodeKind.MatchCountCondition => "count-condition",
            AutomationNodeKind.SystemNotification => "notify",
            _ => "node",
        };
        var id = prefix;
        var suffix = 2;
        var existing = AutomationNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        while (existing.Contains(id))
            id = $"{prefix}-{suffix++}";
        return id;
    }

    private void RefreshConnectorStates()
    {
        foreach (var node in AutomationNodes)
        {
            if (node.InputConnector is not null)
                node.InputConnector.IsConnected = false;
            foreach (var output in node.Output.OfType<AutomationConnectorViewModel>())
                output.IsConnected = false;
        }
        foreach (var connection in Connections)
        {
            connection.Source.IsConnected = true;
            connection.Target.IsConnected = true;
        }
    }

    private void RefreshValidation()
    {
        SyncDefinition();
        var validation = AutomationWorkflowValidator.Validate(_settingsStore.Settings.AutomationWorkflow);
        IsWorkflowValid = validation.IsValid;
        ValidationText = validation.IsValid
            ? $"流程有效：有界 DAG（最多 {AutomationWorkflowValidator.MaximumNodes} 个节点），无循环；仅一条条件分支可产生传输计划。"
            : string.Join("；", validation.Errors);
    }

    private void OnNodeDefinitionChanged()
    {
        if (_buildingGraph)
            return;
        RefreshValidation();
        SchedulePersist();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_buildingGraph)
            return;
        RefreshValidation();
        SchedulePersist();
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshConnectorStates();
        if (_buildingGraph)
            return;
        RefreshValidation();
        SchedulePersist();
    }

    private void OnSelectedNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var selected = SelectedNodes.OfType<AutomationNodeViewModel>().FirstOrDefault();
        if (!ReferenceEquals(selected, SelectedNode))
            SelectedNode = selected;
    }

    private void SchedulePersist()
    {
        if (_buildingGraph)
            return;
        _persistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _persistCts = cts;
        _ = PersistDelayedAsync(cts.Token);
    }

    private async Task PersistDelayedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(true);
            SyncDefinition();
            await _settingsStore.SaveSettingsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task PersistNowAsync()
    {
        _persistCts?.Cancel();
        SyncDefinition();
        try
        {
            await _settingsStore.SaveSettingsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
