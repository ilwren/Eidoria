using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MdExplorer.UI.Controls;

/// <summary>
/// 带一段高亮的文本（v0.48）：内容搜索的摘要要让人一眼看到「为什么命中」。
/// </summary>
/// <remarks>
/// 用 <see cref="TextBlock.Inlines"/> 拼三段 Run（命中前 / 命中 / 命中后）。
/// 偏移量由索引层给出（<c>ContentSearchHit.MatchOffset</c>），所以这里不做任何关键词搜索——
/// 换一种语言或分词规则时不需要改 UI。
/// 三个属性任一变化都重建 Inlines，并用 <c>_rebuilding</c> 挡住自身引发的重入。
/// </remarks>
public sealed class HighlightedText : TextBlock
{
    public static readonly StyledProperty<int> HighlightStartProperty =
        AvaloniaProperty.Register<HighlightedText, int>(nameof(HighlightStart), -1);

    public static readonly StyledProperty<int> HighlightLengthProperty =
        AvaloniaProperty.Register<HighlightedText, int>(nameof(HighlightLength));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightedText, IBrush?>(nameof(HighlightBrush));

    private bool _rebuilding;

    /// <summary>高亮起点（相对 <see cref="TextBlock.Text"/>）；-1 表示整段不高亮。</summary>
    public int HighlightStart
    {
        get => GetValue(HighlightStartProperty);
        set => SetValue(HighlightStartProperty, value);
    }

    public int HighlightLength
    {
        get => GetValue(HighlightLengthProperty);
        set => SetValue(HighlightLengthProperty, value);
    }

    /// <summary>命中片段的前景色；为空时用主色。</summary>
    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_rebuilding)
            return;
        if (change.Property == TextProperty ||
            change.Property == HighlightStartProperty ||
            change.Property == HighlightLengthProperty ||
            change.Property == HighlightBrushProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            var text = Text;
            Inlines?.Clear();
            if (string.IsNullOrEmpty(text))
                return;

            var start = HighlightStart;
            var length = HighlightLength;
            if (start < 0 || length <= 0 || start >= text.Length)
            {
                // 没有命中区间（或区间已失效）：退回普通文本，不要为了高亮把内容弄丢。
                if (Inlines is { } plain)
                    plain.Add(new Run(text));
                return;
            }

            var end = Math.Min(text.Length, start + length);
            var inlines = Inlines ??= new InlineCollection();
            if (start > 0)
                inlines.Add(new Run(text[..start]));
            var highlight = new Run(text[start..end]) { FontWeight = FontWeight.SemiBold };
            if (HighlightBrush is { } brush)
                highlight.Foreground = brush;
            inlines.Add(highlight);
            if (end < text.Length)
                inlines.Add(new Run(text[end..]));
        }
        finally
        {
            _rebuilding = false;
        }
    }
}
