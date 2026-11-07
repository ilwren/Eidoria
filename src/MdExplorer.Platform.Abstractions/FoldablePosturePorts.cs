namespace MdExplorer.Platform.Abstractions;

/// <summary>铰链相对窗口的走向。<see cref="None"/> 表示没有分离式铰链（普通屏幕、展开 180°）。</summary>
public enum FoldHingeAxis
{
    None = 0,

    /// <summary>竖着的铰链：屏幕被分成左右两半（书本式半折叠）。</summary>
    Vertical = 1,

    /// <summary>横着的铰链：屏幕被分成上下两半（桌面式半折叠，像笔记本）。</summary>
    Horizontal = 2,
}

/// <summary>
/// 折叠屏姿态（v0.58）。只描述「设备此刻怎么折」，不含任何布局决策——
/// 布局换算统一走 <see cref="FoldableLayout.Compute"/>，这样几何部分可以脱离设备单测。
/// </summary>
/// <param name="HingeAxis">铰链走向；<see cref="FoldHingeAxis.None"/> 即「没有分离式铰链」。</param>
/// <param name="GutterThicknessDip">
/// 铰链本身占掉的宽度（DIP）。半折叠时这一段是**看不见的**，内容跨过去就会有一半被折进去，
/// 所以要按这个厚度留白。取不到真值时必须给保守估计，不许给 0。
/// </param>
/// <param name="HingeAngleDegrees">
/// 最近一次读到的铰链角度（0–360）。null 表示这根来源不报角度（平铺实现、桌面）。
/// 它只用于「设备姿态」这类诊断展示，不参与布局换算。
/// </param>
/// <param name="IsSensorSupported">这台设备到底有没有铰链传感器：没有就是「普通屏幕」，不是「摊平了」。</param>
public sealed record FoldablePosture(
    FoldHingeAxis HingeAxis,
    double GutterThicknessDip,
    double? HingeAngleDegrees = null,
    bool IsSensorSupported = false)
{
    /// <summary>平铺：没有分离式铰链（绝大多数设备、桌面窗口、展开 180°）。</summary>
    public static FoldablePosture Flat { get; } = new(FoldHingeAxis.None, 0);

    public bool IsHalfFolded => HingeAxis != FoldHingeAxis.None;
}

/// <summary>半折叠时的一次布局换算结果（纯数据，UI 只照着摆）。</summary>
/// <param name="IsHalfFolded">是否处于半折叠（铰链把可视区分成两半）。</param>
/// <param name="Axis">铰链走向。</param>
/// <param name="GutterDip">留给铰链的留白厚度。</param>
/// <param name="PrimaryMarginRight">主区要空出的右侧宽度（竖铰链时 = 副半屏 + 铰链带）。</param>
/// <param name="PrimaryMarginBottom">主区要空出的底部高度（横铰链时 = 副半屏 + 铰链带）。</param>
/// <param name="SecondaryWidth">副半屏宽度（不可用时为 0）。</param>
/// <param name="SecondaryHeight">副半屏高度（不可用时为 0）。</param>
/// <param name="ShowSecondarySurface">副半屏是否够大、值得单独摆一块面板。</param>
public sealed record FoldableLayoutResult(
    bool IsHalfFolded,
    FoldHingeAxis Axis,
    double GutterDip,
    double PrimaryMarginRight,
    double PrimaryMarginBottom,
    double SecondaryWidth,
    double SecondaryHeight,
    bool ShowSecondarySurface)
{
    /// <summary>平铺：什么都不用做。</summary>
    public static FoldableLayoutResult Flat { get; } =
        new(false, FoldHingeAxis.None, 0, 0, 0, 0, 0, false);

    /// <summary>给「矮视口」判定用的参考高度：桌面式半折叠时按**半边**算，不是整窗。</summary>
    public double ShortViewportReferenceHeight(double heightDip) =>
        Axis == FoldHingeAxis.Horizontal && ShowSecondarySurface ? SecondaryHeight : heightDip;
}

/// <summary>
/// 折叠屏姿态换算（纯函数，无平台依赖）。
/// <para>
/// 边界按 <c>docs/DEVELOPMENT.md</c> §7 的断点口径：半折叠时主区只占**第一半**，
/// 副半屏够宽（≥ <see cref="MinSecondaryExtentDip"/>）才单独摆面板；不够就老实放弃副屏，
/// 绝不把主区压到比断点还窄。
/// </para>
/// </summary>
public static class FoldableLayout
{
    /// <summary>半折叠的角度区间（不含端点）：0° 是合起，180° 是摊平，中间都算分离。</summary>
    public const double HalfFoldMinAngle = 30;

    public const double HalfFoldMaxAngle = 150;

    /// <summary>
    /// 铰链留白的默认厚度（DIP）。铰链传感器的角度值里没有「铰链多宽」这个信息，
    /// 而这一段在半折叠时是看不见的；宁可多留一条窄缝，也不让内容被折进去。
    /// </summary>
    public const double DefaultGutterDip = 32;

    /// <summary>留白上限：再宽就不是铰链而是折痕误判了。</summary>
    public const double MaxGutterDip = 96;

    /// <summary>副半屏可用的最小边长（DIP）；低于这个数就只保留主区，不硬塞一块窄板。</summary>
    public const double MinSecondaryExtentDip = 260;

    /// <summary>角度是否落在「半折叠」区间内。</summary>
    public static bool IsHalfFoldedAngle(double degrees) =>
        degrees > HalfFoldMinAngle && degrees < HalfFoldMaxAngle;

    /// <summary>
    /// 由铰链角度 + 窗口是否横置推出姿态。
    /// <para>
    /// 窗口横着（宽 ≥ 高）时铰链按横向处理：半折叠的笔记本姿态就是上下两半；
    /// 竖着则是书本姿态、左右两半。铰链角度传感器本身不报走向，这是最实用的推断，
    /// 判错的代价只是「主副两半换了方向」，不会出现内容被折进去的情况。
    /// </para>
    /// </summary>
    public static FoldablePosture FromHingeAngle(double degrees, bool isLandscape, double gutterThicknessDip = DefaultGutterDip)
    {
        // 摊平时仍把角度与「传感器可用」带上：设置页要能显示「平铺（铰链角 178°）」，
        // 而不是让人分不清「没有传感器」和「摊平了」。
        return new FoldablePosture(
            IsHalfFoldedAngle(degrees)
                ? (isLandscape ? FoldHingeAxis.Horizontal : FoldHingeAxis.Vertical)
                : FoldHingeAxis.None,
            gutterThicknessDip,
            degrees,
            IsSensorSupported: true);
    }

    /// <summary>把姿态换算成一次布局（视口尺寸单位为 DIP）。</summary>
    public static FoldableLayoutResult Compute(FoldablePosture? posture, double widthDip, double heightDip)
    {
        if (posture is null || !posture.IsHalfFolded || widthDip <= 0 || heightDip <= 0)
            return FoldableLayoutResult.Flat;

        var gutter = Math.Clamp(posture.GutterThicknessDip, 0, MaxGutterDip);

        if (posture.HingeAxis == FoldHingeAxis.Vertical)
        {
            var half = (widthDip - gutter) / 2;
            var show = half >= MinSecondaryExtentDip;
            return new FoldableLayoutResult(
                IsHalfFolded: true,
                Axis: FoldHingeAxis.Vertical,
                GutterDip: gutter,
                PrimaryMarginRight: show ? widthDip - half : 0,
                PrimaryMarginBottom: 0,
                SecondaryWidth: show ? half : 0,
                SecondaryHeight: show ? heightDip : 0,
                ShowSecondarySurface: show);
        }

        var halfHeight = (heightDip - gutter) / 2;
        var showTabletop = halfHeight >= MinSecondaryExtentDip;
        return new FoldableLayoutResult(
            IsHalfFolded: true,
            Axis: FoldHingeAxis.Horizontal,
            GutterDip: gutter,
            PrimaryMarginRight: 0,
            PrimaryMarginBottom: showTabletop ? heightDip - halfHeight : 0,
            SecondaryWidth: showTabletop ? widthDip : 0,
            SecondaryHeight: showTabletop ? halfHeight : 0,
            ShowSecondarySurface: showTabletop);
    }
}

/// <summary>
/// 折叠屏姿态来源（v0.58）。Android 宿主用铰链角度传感器实现；桌面与无头测试用平铺实现。
/// <para>
/// 契约：<see cref="Current"/> 永远返回一个有效值（取不到就是 <see cref="FoldablePosture.Flat"/>），
/// <see cref="Changed"/> 在姿态真的变了之后触发（同一姿态的重复上报可以忽略）。
/// 实现方不可抛异常——姿态是锦上添花，不能把界面拖下水。
/// </para>
/// </summary>
public interface IFoldablePostureSource
{
    FoldablePosture Current { get; }

    event EventHandler? Changed;
}
