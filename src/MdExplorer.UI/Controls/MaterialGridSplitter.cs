using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace MdExplorer.UI.Controls;

/// <summary>
/// Material 风格的窗格分隔条原型：在 Md3.Avalonia 正式提供同类组件前，先在 Eidoria 内验证
/// 细轨道 + 居中胶囊把手 + 键盘可达三件套。拖动/键盘增量行为复用 Avalonia GridSplitter，
/// 视觉与状态反馈由本控件的 ControlTheme 承担；成熟后可整体回流 Md3.Avalonia。
/// </summary>
[TemplatePart("PART_Handle", typeof(Border))]
[TemplatePart("PART_Track", typeof(Border))]
[PseudoClasses(":pointerover", ":pressed", ":focus-visible", ":reduce-motion")]
public sealed class MaterialGridSplitter : GridSplitter
{
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<MaterialGridSplitter, bool>(nameof(ReduceMotion));

    private Border? _handle;
    private Border? _track;
    private Transitions? _handleTransitions;
    private Transitions? _trackTransitions;

    /// <summary>宿主低动效偏好；开启时禁用模板部件的尺寸/颜色过渡，并以 :reduce-motion 供主题收敛状态形变。</summary>
    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }
    static MaterialGridSplitter()
    {
        InputElement.FocusableProperty.OverrideDefaultValue<MaterialGridSplitter>(true);
    }

    /// <summary>用户双击把手或在键盘焦点下按 Home 时触发；宿主据此把相邻窗格恢复为自己的默认宽度。</summary>
    public event EventHandler? ResetRequested;

    public MaterialGridSplitter()
    {
        ResizeDirection = GridResizeDirection.Columns;
        KeyboardIncrement = 8;
        DoubleTapped += (_, e) =>
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Home)
                return;
            ResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _handle = e.NameScope.Find<Border>("PART_Handle");
        _track = e.NameScope.Find<Border>("PART_Track");
        _handleTransitions = _handle?.Transitions;
        _trackTransitions = _track?.Transitions;
        ApplyMotionPreference();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReduceMotionProperty)
            ApplyMotionPreference();
    }

    private void ApplyMotionPreference()
    {
        PseudoClasses.Set(":reduce-motion", ReduceMotion);
        if (_handle is not null)
            _handle.Transitions = ReduceMotion ? null : _handleTransitions;
        if (_track is not null)
            _track.Transitions = ReduceMotion ? null : _trackTransitions;
    }
}
