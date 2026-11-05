using Avalonia.Controls;
using Avalonia.Threading;

namespace MdExplorer.UI.Views;

/// <summary>
/// 自动化节点编辑器。响应式只重排画布/检查器：宽屏并排以便边连线边改参数，Compact 时
/// 画布在上、检查器在下且页面整体可拖动滚动；不按窗口等比缩放整页。
/// </summary>
public partial class AutomationView : UserControl
{
    private bool _isStacked;

    public AutomationView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        AutomationEditor.Loaded += (_, _) =>
            Dispatcher.UIThread.Post(ApplyEditorZoom, DispatcherPriority.Loaded);
    }

    private void ApplyLayout(double width)
    {
        var stacked = width > 0 && width < 900;
        if (stacked == _isStacked && width > 0)
            return;
        _isStacked = stacked;

        AutomationLayoutGrid.ColumnDefinitions.Clear();
        AutomationLayoutGrid.RowDefinitions.Clear();
        if (stacked)
        {
            AutomationLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            AutomationLayoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AutomationLayoutGrid.RowDefinitions.Add(new RowDefinition(new GridLength(14)));
            AutomationLayoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(AutomationCanvasCard, 0);
            Grid.SetColumn(AutomationCanvasCard, 0);
            Grid.SetRow(AutomationInspectorCard, 2);
            Grid.SetColumn(AutomationInspectorCard, 0);
            AutomationEditorFrame.Height = 380;
        }
        else
        {
            AutomationLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            AutomationLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(16)));
            AutomationLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(340)));
            AutomationLayoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(AutomationCanvasCard, 0);
            Grid.SetColumn(AutomationCanvasCard, 0);
            Grid.SetRow(AutomationInspectorCard, 0);
            Grid.SetColumn(AutomationInspectorCard, 2);
            AutomationEditorFrame.Height = 520;
        }

        if (AutomationEditor.IsLoaded)
            Dispatcher.UIThread.Post(ApplyEditorZoom, DispatcherPriority.Loaded);
    }

    private void ApplyEditorZoom()
    {
        // NodifyEditor.OnLoaded 会按整图宽高自动“塞满”视口；横向四节点链在 Compact 会被
        // 压到约 0.3 倍，连接点小到无法触摸。这里在上游 Loaded 之后恢复可操作缩放：
        // 窄屏保留前段节点并靠拖动画布平移，宽屏使用既有总览倍率。
        var zoom = _isStacked ? 0.68 : 0.82;
        if (Math.Abs(AutomationEditor.Zoom - zoom) > 0.001)
            AutomationEditor.Zoom = zoom;

        AutomationEditor.OffsetX = 0;
        AutomationEditor.OffsetY = 0;
        AutomationEditor.ViewTranslateTransform.X = 0;
        AutomationEditor.ViewTranslateTransform.Y = 0;
    }
}
