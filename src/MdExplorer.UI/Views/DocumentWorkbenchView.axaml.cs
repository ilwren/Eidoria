using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using MdExplorer.Domain;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class DocumentWorkbenchView : UserControl
{
    private DocumentWorkbenchViewModel? _vm;
    private bool _syncingEditor;
    private bool _syncingPdfPageFromContinuousScroll;

    public DocumentWorkbenchView()
    {
        InitializeComponent();
        DocumentEditor.TextChanged += OnEditorTextChanged;
        DocumentSearchButton.Click += (_, _) => OpenSearchPanel();
        DocumentUndoButton.Click += (_, _) =>
        {
            DocumentEditor.Undo();
            DocumentEditor.Focus();
        };
        DocumentRedoButton.Click += (_, _) =>
        {
            DocumentEditor.Redo();
            DocumentEditor.Focus();
        };
        PdfPageList.ContainerPrepared += OnPdfPageContainerPrepared;
        PdfContinuousList.ContainerPrepared += OnPdfContinuousContainerPrepared;
        PdfContinuousList.AddHandler(ScrollViewer.ScrollChangedEvent, OnPdfContinuousScrollChanged);
        PdfPageInputBox.KeyDown += OnPdfPageInputKeyDown;
        PdfPasswordBox.KeyDown += OnPdfPasswordKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DocumentWorkbenchViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            ApplyEditorState();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width <= 0)
            return;
        var compact = e.NewSize.Width < 720;
        if (_vm is not null)
            _vm.IsCompactLayout = compact;
        DocumentRoot.Margin = compact ? new Thickness(10, 8, 10, 12) : new Thickness(20, 12, 20, 20);
        PdfThumbnailRail.IsVisible = !compact;
        // 窄屏只保留翻页、跳页与缩放；首末页、旋转和系统打开收起，避免固定列工具栏横向溢出。
        var showPdfSecondaryActions = e.NewSize.Width >= 640;
        PdfFirstPageButton.IsVisible = showPdfSecondaryActions;
        PdfLastPageButton.IsVisible = showPdfSecondaryActions;
        PdfRotateButton.IsVisible = showPdfSecondaryActions;
        PdfOpenExternalButton.IsVisible = showPdfSecondaryActions;
        PdfWorkbenchGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 140);
        PdfWorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 12);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_vm is null)
        {
            base.OnKeyDown(e);
            return;
        }

        if (_vm.IsTextDocument && e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = _vm.SaveCommand.ExecuteAsync(null);
            e.Handled = true;
            return;
        }

        if (_vm.IsTextDocument && e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenSearchPanel();
            e.Handled = true;
            return;
        }

        if (_vm.ShowPdfWorkbench && e.Source is not TextBox)
        {
            // 连续模式让方向键回到列表自身的滚动/选择语义；PageUp/PageDown 仍按整页导航。
            var next = _vm.IsContinuousPdfMode
                ? e.Key == Key.PageDown
                : e.Key is Key.PageDown or Key.Right or Key.Down;
            var previous = _vm.IsContinuousPdfMode
                ? e.Key == Key.PageUp
                : e.Key is Key.PageUp or Key.Left or Key.Up;
            if (next)
            {
                _vm.NextPdfPageCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (previous)
            {
                _vm.PreviousPdfPageCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentWorkbenchViewModel.EditorText)
            or nameof(DocumentWorkbenchViewModel.HighlightLanguage)
            or nameof(DocumentWorkbenchViewModel.IsEditing)
            or nameof(DocumentWorkbenchViewModel.FullPath))
            ApplyEditorState();

        if (!_syncingPdfPageFromContinuousScroll &&
            (e.PropertyName is nameof(DocumentWorkbenchViewModel.PdfPageIndex)
                or nameof(DocumentWorkbenchViewModel.PdfReadingModeIndex)))
        {
            // 等连续列表先完成本帧布局，再把目标页带入视口；滚动同步页码时不反向抢滚动位置。
            Dispatcher.UIThread.Post(ScrollContinuousToCurrentPage, DispatcherPriority.Loaded);
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditor || _vm is null)
            return;
        _vm.SetEditorTextFromView(DocumentEditor.Text ?? string.Empty);
    }

    private void ApplyEditorState()
    {
        if (_vm is null)
            return;

        _syncingEditor = true;
        try
        {
            var nextText = _vm.EditorText ?? string.Empty;
            if (!string.Equals(DocumentEditor.Text, nextText, StringComparison.Ordinal))
                DocumentEditor.Text = nextText;
            DocumentEditor.IsReadOnly = !_vm.IsEditing;
            DocumentEditor.WordWrap = _vm.CurrentItem?.Kind != FileKind.Code;
            var language = _vm.HighlightLanguage;
            DocumentEditor.SyntaxHighlighting = string.IsNullOrEmpty(language)
                ? null
                : HighlightingManager.Instance.GetDefinition(language);
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void OpenSearchPanel()
    {
        DocumentEditor.Focus();
        DocumentEditor.SearchPanel?.Open();
    }

    private void OnPdfPageContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (_vm is not null && e.Container.DataContext is PdfPageViewModel page)
            _ = _vm.EnsureThumbnailAsync(page);
    }

    private void OnPdfContinuousContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (_vm is not null && e.Container.DataContext is PdfPageViewModel page)
            _ = _vm.EnsurePageImageAsync(page);
    }

    private void ScrollContinuousToCurrentPage()
    {
        if (_vm is null || !_vm.IsContinuousPdfMode || _syncingPdfPageFromContinuousScroll ||
            _vm.PdfPageIndex < 0 || _vm.PdfPageIndex >= _vm.PdfPages.Count)
            return;
        PdfContinuousList.ScrollIntoView(_vm.PdfPages[_vm.PdfPageIndex]);
    }

    private void OnPdfContinuousScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || !_vm.IsContinuousPdfMode || PdfContinuousList.Bounds.Height <= 0)
            return;

        var anchorY = Math.Clamp(PdfContinuousList.Bounds.Height * 0.25, 24, 160);
        PdfPageViewModel? anchored = null;
        PdfPageViewModel? firstVisible = null;
        foreach (var item in PdfContinuousList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not PdfPageViewModel page)
                continue;
            var top = item.TranslatePoint(new Point(0, 0), PdfContinuousList)?.Y;
            if (top is not { } y)
                continue;
            var bottom = y + item.Bounds.Height;
            if (bottom <= 0 || y >= PdfContinuousList.Bounds.Height)
                continue;
            if (firstVisible is null || page.Index < firstVisible.Index)
                firstVisible = page;
            if (y <= anchorY && bottom > anchorY)
            {
                anchored = page;
                break;
            }
        }

        var current = anchored ?? firstVisible;
        if (current is null || current.Index == _vm.PdfPageIndex)
            return;
        _syncingPdfPageFromContinuousScroll = true;
        try
        {
            _vm.PdfPageIndex = current.Index;
        }
        finally
        {
            _syncingPdfPageFromContinuousScroll = false;
        }
    }

    private void OnPdfPageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _vm.GoToPdfPageInputCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPdfPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _ = _vm.UnlockPdfCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
