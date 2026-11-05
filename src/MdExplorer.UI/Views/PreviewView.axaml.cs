using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit.Highlighting;
using MdExplorer.UI.ViewModels;

namespace MdExplorer.UI.Views;

public partial class PreviewView : UserControl
{
    private PreviewViewModel? _vm;

    public PreviewView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as PreviewViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            ApplySource();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.TextContent) or nameof(PreviewViewModel.HighlightLanguage))
            ApplySource();
    }

    /// <summary>KeePass 分组/条目行没有独立的选中控件，点击即交给 VM 切换（同压缩包面包屑的既定做法）。</summary>
    private void OnKeePassGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeePassGroupRowModel group } && _vm is not null)
            _vm.SelectKeePassGroup(group);
        e.Handled = true;
    }

    private void OnKeePassEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeePassEntryRowModel entry } && _vm is not null)
            _vm.SelectKeePassEntry(entry);
        e.Handled = true;
    }

    /// <summary>把 VM 的源码文本/语种同步进只读编辑器（TextEditor 非绑定属性，代码后置是既定用法）。</summary>
    private void ApplySource()
    {
        if (_vm is null) return;
        CodeEditor.Text = _vm.TextContent ?? string.Empty;
        var language = _vm.HighlightLanguage;
        CodeEditor.SyntaxHighlighting = string.IsNullOrEmpty(language)
            ? null
            : HighlightingManager.Instance.GetDefinition(language);
    }
}
