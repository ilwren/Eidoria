using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 平铺实现（v0.58）：桌面窗口、无头测试、以及没登记折叠检测的宿主都用它。
/// <para>
/// 它的存在本身就是契约的一部分——「取不到姿态」与「设备摊平」在布局上是同一件事，
/// 都不该触发布局变化，所以不必让调用方到处判 null。
/// </para>
/// </summary>
public sealed class FlatFoldablePostureSource : IFoldablePostureSource
{
    public static FlatFoldablePostureSource Instance { get; } = new();

    public FoldablePosture Current => FoldablePosture.Flat;

    /// <summary>永远是平铺，不会有变化；保留事件只为满足端口契约。</summary>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
