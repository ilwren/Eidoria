using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Services;
using MdExplorer.UI.ViewModels;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// v0.58：折叠屏半折叠布局。几何换算是纯函数，先在单测里钉死；
/// 再拿一个假的姿态来源喂给组合根，确认界面那层（主区留白、副半屏、矮视口）真的跟着变。
/// </summary>
public sealed class FoldablePostureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eidoria-fold-" + Guid.NewGuid().ToString("N"));

    public FoldablePostureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── 姿态判定 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0d)]
    [InlineData(20d)]
    [InlineData(160d)]
    [InlineData(180d)]
    public void FlatAnglesAreNotHalfFolded(double degrees)
    {
        Assert.False(FoldableLayout.IsHalfFoldedAngle(degrees));
        // 摊平的读数仍要带上角度与「传感器可用」：设置页要能区分「没有传感器」和「摊平了」。
        var posture = FoldableLayout.FromHingeAngle(degrees, isLandscape: true);
        Assert.False(posture.IsHalfFolded);
        Assert.True(posture.IsSensorSupported);
        Assert.Equal(degrees, posture.HingeAngleDegrees ?? double.NaN);
    }

    [Theory]
    [InlineData(45d)]
    [InlineData(90d)]
    [InlineData(135d)]
    public void AnglesBetweenBecomeHalfFolded(double degrees)
    {
        Assert.True(FoldableLayout.IsHalfFoldedAngle(degrees));
        var posture = FoldableLayout.FromHingeAngle(degrees, isLandscape: false);
        Assert.True(posture.IsHalfFolded);
        Assert.Equal(FoldHingeAxis.Vertical, posture.HingeAxis);
    }

    [Fact]
    public void LandscapeWindowsGetAHorizontalHinge()
    {
        // 横窗=笔记本姿态：上下两半。竖窗=书本姿态：左右两半。
        Assert.Equal(FoldHingeAxis.Horizontal, FoldableLayout.FromHingeAngle(90, isLandscape: true).HingeAxis);
        Assert.Equal(FoldHingeAxis.Vertical, FoldableLayout.FromHingeAngle(90, isLandscape: false).HingeAxis);
    }

    // ── 布局换算 ──────────────────────────────────────────────────────────

    [Fact]
    public void FlatPostureChangesNothing()
    {
        var layout = FoldableLayout.Compute(FoldablePosture.Flat, 1200, 841);

        Assert.False(layout.IsHalfFolded);
        Assert.Equal(0, layout.PrimaryMarginRight);
        Assert.Equal(0, layout.PrimaryMarginBottom);
        Assert.False(layout.ShowSecondarySurface);
        Assert.Equal(841, layout.ShortViewportReferenceHeight(841));
    }

    [Fact]
    public void BookPostureReservesTheRightHalfAndAGutter()
    {
        const double gutter = 32;
        var layout = FoldableLayout.Compute(new FoldablePosture(FoldHingeAxis.Vertical, gutter), 1200, 841);

        Assert.True(layout.IsHalfFolded);
        Assert.Equal(FoldHingeAxis.Vertical, layout.Axis);
        Assert.Equal(0, layout.PrimaryMarginBottom);
        // 左边一半 + 铰链带都归主区「让出」：右半边 584，让出 1200-584=616。
        Assert.Equal(584, layout.SecondaryWidth, 3);
        Assert.Equal(616, layout.PrimaryMarginRight, 3);
        Assert.Equal(841, layout.SecondaryHeight, 3);
        Assert.True(layout.ShowSecondarySurface);
        // 书本式半折叠不该把整窗高度当成「矮」，它本来就不矮。
        Assert.Equal(841, layout.ShortViewportReferenceHeight(841));
    }

    [Fact]
    public void TabletopPostureReservesTheBottomHalfAndShortensTheViewport()
    {
        const double gutter = 32;
        var layout = FoldableLayout.Compute(new FoldablePosture(FoldHingeAxis.Horizontal, gutter), 1200, 841);

        Assert.Equal(0, layout.PrimaryMarginRight);
        Assert.Equal(404.5, layout.SecondaryHeight, 3);
        Assert.Equal(436.5, layout.PrimaryMarginBottom, 3);
        Assert.Equal(1200, layout.SecondaryWidth, 3);
        // 桌面式半折叠时内容只有 404.5 高——矮视口判定必须按半边算。
        Assert.Equal(404.5, layout.ShortViewportReferenceHeight(841), 3);
    }

    [Fact]
    public void TooNarrowSecondaryHalfIsDroppedInsteadOfCramped()
    {
        // 500 宽减掉 32 的铰链带，每半只有 234——比副半屏最小边长还窄，就不硬塞面板了。
        var layout = FoldableLayout.Compute(new FoldablePosture(FoldHingeAxis.Vertical, 32), 500, 800);

        Assert.True(layout.IsHalfFolded);
        Assert.False(layout.ShowSecondarySurface);
        Assert.Equal(0, layout.PrimaryMarginRight);
    }

    [Fact]
    public void AbsurdGutterIsClampedSoThePrimaryHalfSurvives()
    {
        var layout = FoldableLayout.Compute(new FoldablePosture(FoldHingeAxis.Vertical, 4000), 1200, 800);

        Assert.Equal(FoldableLayout.MaxGutterDip, layout.GutterDip);
        Assert.True(layout.ShowSecondarySurface);
        Assert.True(layout.SecondaryWidth > FoldableLayout.MinSecondaryExtentDip);
    }

    [Fact]
    public void UnknownSizesFallBackToFlat()
    {
        // 视图还没量到尺寸（0）时不许猜：退回平铺，等真实尺寸来了再算。
        Assert.Equal(FoldableLayoutResult.Flat,
            FoldableLayout.Compute(new FoldablePosture(FoldHingeAxis.Vertical, 32), 0, 0));
    }

    // ── 界面层 ────────────────────────────────────────────────────────────

    [Fact]
    public void ShellReservesTheHingeBandOnlyWhileHalfFolded()
    {
        var fake = new FakeFoldablePostureSource();
        var shell = AppHost.CreateShell(AppHost.Build(
            Directory.CreateDirectory(Path.Combine(_root, "data")).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "start")).FullName,
            foldablePosture: fake));

        shell.SetViewportSize(1200, 841);
        Assert.False(shell.IsHalfFolded);
        Assert.Equal(new Avalonia.Thickness(0), shell.FoldPrimaryMargin);

        fake.Set(new FoldablePosture(FoldHingeAxis.Vertical, 32));
        shell.SetViewportSize(1200, 841);
        Assert.True(shell.IsHalfFolded);
        Assert.True(shell.IsFoldSecondaryBook);
        Assert.False(shell.IsFoldSecondaryTabletop);
        Assert.Equal(616, shell.FoldPrimaryMargin.Right, 3);
        Assert.Equal(0, shell.FoldPrimaryMargin.Bottom);
        Assert.Equal(584, shell.FoldSecondaryWidth, 3);
        // 主区只有 584 宽：断点必须按主区算（584 → Compact），否则抽屉+预览栏会一起塞进半屏里。
        // 但底栏挂在窗口最底下、会横跨铰链，所以半折叠时一律不起底栏，导航退成导轨。
        Assert.True(shell.IsCompact);
        Assert.True(shell.ShowRail);
        Assert.False(shell.ShowBottomNav);
        Assert.False(shell.ShowDrawerPane);
        // 常驻预览栏让位给副半屏，两半各摆一套预览只会互相挤。
        Assert.False(shell.ShowPreviewPaneNow);
        // 副半屏空着时给说明，不要留一块让人以为卡住的空白。
        Assert.True(shell.ShowFoldSecondaryHint);
        Assert.False(shell.ShowFoldSecondaryPreview);
        // 没内容可显示时不造第二份预览视图（媒体画面只许挂一处）。
        Assert.Null(shell.FoldSecondaryPreviewSource);

        fake.Set(FoldablePosture.Flat);
        shell.SetViewportSize(1200, 841);
        Assert.True(shell.IsLargeOrAbove);
        Assert.False(shell.IsHalfFolded);
        Assert.False(shell.ShowFoldSecondarySurface);
        Assert.Equal(new Avalonia.Thickness(0), shell.FoldPrimaryMargin);
    }

    [Fact]
    public void DiagnosticsSeparateNoSensorFromFlatPosture()
    {
        // 设置页「关于」里的那行设备姿态：真机上靠它判断铰链到底有没有被识别。
        var noSensor = AppHost.CreateShell(AppHost.Build(
            Directory.CreateDirectory(Path.Combine(_root, "d1")).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "s1")).FullName,
            foldablePosture: new FakeFoldablePostureSource()));
        Assert.Contains("未检测到铰链传感器", noSensor.FoldableDiagnosticText, StringComparison.Ordinal);

        var flatWithSensor = AppHost.CreateShell(AppHost.Build(
            Directory.CreateDirectory(Path.Combine(_root, "d2")).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "s2")).FullName,
            foldablePosture: new FakeFoldablePostureSource(
                FoldableLayout.FromHingeAngle(178, isLandscape: true))));
        Assert.Contains("平铺", flatWithSensor.FoldableDiagnosticText, StringComparison.Ordinal);
        Assert.Contains("178", flatWithSensor.FoldableDiagnosticText, StringComparison.Ordinal);

        var book = AppHost.CreateShell(AppHost.Build(
            Directory.CreateDirectory(Path.Combine(_root, "d3")).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "s3")).FullName,
            foldablePosture: new FakeFoldablePostureSource(
                FoldableLayout.FromHingeAngle(92, isLandscape: false))));
        Assert.Contains("书本式半折叠", book.FoldableDiagnosticText, StringComparison.Ordinal);
        Assert.Contains("92", book.FoldableDiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public void TabletopHalfFoldCountsAsShortViewport()
    {
        var fake = new FakeFoldablePostureSource(
            new FoldablePosture(FoldHingeAxis.Horizontal, 32));
        var shell = AppHost.CreateShell(AppHost.Build(
            Directory.CreateDirectory(Path.Combine(_root, "data2")).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "start2")).FullName,
            foldablePosture: fake));

        // 整窗 841 不算矮；但横铰链把内容压到 404.5，那就必须按矮视口处理：
        // 抽屉收起来、底部导航不出现，导航退成导轨。
        shell.SetViewportSize(1200, 841);
        Assert.True(shell.IsHalfFolded);
        Assert.True(shell.IsShortViewport);
        Assert.False(shell.ShowDrawerPane);
        Assert.False(shell.ShowBottomNav);
        Assert.True(shell.ShowRail);
        Assert.True(shell.IsFoldSecondaryTabletop);
        Assert.Equal(436.5, shell.FoldPrimaryMargin.Bottom, 3);
        Assert.Equal(404.5, shell.FoldSecondaryHeight, 3);
    }
}

/// <summary>测试用姿态来源：想让它报什么就报什么（真实设备由 Android 的铰链传感器实现）。</summary>
internal sealed class FakeFoldablePostureSource : IFoldablePostureSource
{
    public FakeFoldablePostureSource(FoldablePosture? initial = null) => Current = initial ?? FoldablePosture.Flat;

    public FoldablePosture Current { get; private set; }

    public event EventHandler? Changed;

    public void Set(FoldablePosture posture)
    {
        if (posture.Equals(Current))
            return;
        Current = posture;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
