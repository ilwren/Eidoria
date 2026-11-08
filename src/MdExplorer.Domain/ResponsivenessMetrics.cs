using System.Diagnostics;

namespace MdExplorer.Domain;

/// <summary>
/// 帧节奏统计（v0.60）：把「看着不流畅」变成可复现的数字。
/// <para>
/// 只做算术，不碰 UI、不碰解码器：解码线程 / UI 线程按到达顺序喂时间戳进来，
/// 这里算帧率、帧间隔分位数、掉帧数与首帧时延。视频"卡不卡"在人眼上是一个
/// <b>间隔分布</b>问题，平均值会骗人（30 帧均匀掉 1 帧与连续掉 3 帧平均值一样），
/// 所以报告里给的是 p50 / p95 / 最大间隔与「超过 1.5 倍目标周期的间隔」计数。
/// </para>
/// <para>
/// 线程约定：调用方串行喂样本（解码线程自己排队），本类不加锁。
/// </para>
/// </summary>
public sealed class FramePacingStats
{
    private readonly List<double> _intervalsMs;
    private readonly int _capacity;
    private long _lastTimestamp;
    private long _firstFrameAt;
    private long _firstFrameStartedAt;
    private int _droppedFrames;
    private int _overruns;
    private double _maxOverrunMs;

    /// <param name="targetFps">目标帧率；用来算「理想周期」和掉帧判定。</param>
    /// <param name="capacity">只保留最近多少个间隔样本（环形语义：超了就丢最旧的）。</param>
    public FramePacingStats(double targetFps = 30d, int capacity = 600)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetFps, 0d);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 8);
        TargetFps = targetFps;
        _capacity = capacity;
        _intervalsMs = new List<double>(capacity);
    }

    public double TargetFps { get; }

    /// <summary>接受一帧时的时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
    public void Record(long timestamp)
    {
        if (_lastTimestamp != 0)
        {
            var intervalMs = (timestamp - _lastTimestamp) * 1000d / Stopwatch.Frequency;
            if (intervalMs >= 0)
            {
                if (_intervalsMs.Count >= _capacity)
                    _intervalsMs.RemoveAt(0);
                _intervalsMs.Add(intervalMs);
                // 掉帧判据：间隔超过理想周期的 1.5 倍。1.5 而不是 1.0，是为了不把
                // 正常的调度抖动（16.7 → 20 ms）算成掉帧，否则报告永远是红的。
                if (intervalMs > 1500d / TargetFps)
                    _droppedFrames++;
            }
        }

        _lastTimestamp = timestamp;
        if (_firstFrameAt == 0)
            _firstFrameAt = timestamp;
    }

    /// <summary>等待首帧的起点（例如「按下播放」或「挂上帧呈现面」的时刻）。</summary>
    public void MarkFirstFramePending(long startedAt) => _firstFrameStartedAt = startedAt;

    public int SampleCount => _intervalsMs.Count;

    /// <summary>窗口内实际帧率：按平均间隔折算。没有样本时返回 0。</summary>
    public double Fps =>
        _intervalsMs.Count == 0 ? 0d : 1000d / _intervalsMs.Average();

    /// <summary>最大间隔（毫秒）：最刺眼的那一次卡顿。</summary>
    public double MaxIntervalMs => _intervalsMs.Count == 0 ? 0d : _intervalsMs.Max();

    /// <summary>间隔分位数（毫秒）。<paramref name="percentile"/> 取 0–1。</summary>
    public double IntervalPercentileMs(double percentile)
    {
        if (_intervalsMs.Count == 0)
            return 0d;
        var sorted = _intervalsMs.ToArray();
        Array.Sort(sorted);
        var index = (int)Math.Clamp(Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>超过 1.5 倍理想周期的帧数（累计，不随窗口淘汰而减少）。</summary>
    public int DroppedFrames => _droppedFrames;

    /// <summary>
    /// 记录一次「单帧处理超过目标周期」：解码 + 呈现本身就跟不上，而不是调度抖动。
    /// 与 <see cref="DroppedFrames"/> 分开记，是因为两者的修法完全不同：
    /// 前者要降分辨率/换采样策略，后者要看 UI 线程有没有被别的东西占住。
    /// </summary>
    public void RecordOverrun(double elapsedMs)
    {
        _overruns++;
        if (elapsedMs > _maxOverrunMs)
            _maxOverrunMs = elapsedMs;
    }

    /// <summary>单帧处理超时的次数（解码侧瓶颈）。</summary>
    public int Overruns => _overruns;

    /// <summary>单帧处理超时的最长一次（毫秒）。</summary>
    public double MaxOverrunMs => _maxOverrunMs;

    /// <summary>首帧时延（毫秒）：从 <see cref="MarkFirstFramePending"/> 到第一帧。</summary>
    public double FirstFrameMs
    {
        get
        {
            if (_firstFrameAt == 0)
                return -1;
            // 没标起点时退回「从第一帧开始的窗口」，此时首帧时延无从谈起，报 -1。
            if (_firstFrameStartedAt == 0)
                return -1;
            return (_firstFrameAt - _firstFrameStartedAt) * 1000d / Stopwatch.Frequency;
        }
    }

    public bool HasSamples => _intervalsMs.Count > 0;

    /// <summary>一句话摘要；数字缺项时如实说「还未出画面」，不写 0。</summary>
    public string Describe()
    {
        if (!HasSamples)
            return FirstFrameMs < 0 ? "帧：还没有画面" : $"帧：首帧 {FirstFrameMs:F0} ms，还没有第二帧";

        var first = FirstFrameMs >= 0 ? $"首帧 {FirstFrameMs:F0} ms · " : string.Empty;
        var overrun = Overruns > 0 ? $" · 解码超时 {Overruns} 次（最长 {MaxOverrunMs:F0} ms）" : string.Empty;
        return $"{first}{Fps:F1} fps · 间隔 p50 {IntervalPercentileMs(0.5):F0} ms / " +
               $"p95 {IntervalPercentileMs(0.95):F0} ms / 最大 {MaxIntervalMs:F0} ms · " +
               $"掉帧 {DroppedFrames}（目标 {TargetFps:F0} fps，样本 {SampleCount}）{overrun}";
    }

    public void Reset()
    {
        _intervalsMs.Clear();
        _lastTimestamp = 0;
        _firstFrameAt = 0;
        _firstFrameStartedAt = 0;
        _droppedFrames = 0;
        _overruns = 0;
        _maxOverrunMs = 0d;
    }
}

/// <summary>
/// UI 线程停顿记录器（v0.60）：索引 / 大搜索这类长任务期间，UI 线程是否被卡住、
/// 卡了多久，靠它量。调用方按固定间隔（建议 16 ms）在 UI 线程上喂一个时间戳；
/// 两次回调之间的间隔减去计划间隔，就是这一次"UI 被占住"的时长。
/// <para>
/// 只保留统计量，不保留原始样本，长任务（几十分钟）也不会把内存拖起来。
/// </para>
/// </summary>
public sealed class UiStallRecorder
{
    private readonly double _intervalMs;
    private readonly double _stallThresholdMs;
    private readonly List<double> _stalls = [];
    private readonly int _capacity;
    private long _lastTimestamp;
    private int _ticks;
    private double _maxStallMs;
    private int _longStalls;

    /// <param name="intervalMs">调用间隔（毫秒），与喂样本的定时器周期一致。</param>
    /// <param name="stallThresholdMs">超过它算一次"长任务"（默认 50 ms = 人感知到卡顿的门槛）。</param>
    /// <param name="capacity">分位数统计的样本上限。</param>
    public UiStallRecorder(double intervalMs = 16d, double stallThresholdMs = 50d, int capacity = 4000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalMs, 0d);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stallThresholdMs, 0d);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _intervalMs = intervalMs;
        _stallThresholdMs = stallThresholdMs;
        _capacity = capacity;
    }

    public double StallThresholdMs => _stallThresholdMs;

    /// <summary>喂一个 UI 线程时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
    public void Record(long timestamp)
    {
        _ticks++;
        if (_lastTimestamp != 0)
        {
            var elapsedMs = (timestamp - _lastTimestamp) * 1000d / Stopwatch.Frequency;
            if (elapsedMs > _intervalMs)
            {
                var stall = elapsedMs - _intervalMs;
                if (_stalls.Count >= _capacity)
                    _stalls.RemoveAt(0);
                _stalls.Add(stall);
                if (stall > _maxStallMs)
                    _maxStallMs = stall;
                if (stall >= _stallThresholdMs)
                    _longStalls++;
            }
        }

        _lastTimestamp = timestamp;
    }

    public int Ticks => _ticks;

    /// <summary>超过阈值的长停顿次数。</summary>
    public int LongStalls => _longStalls;

    public double MaxStallMs => _maxStallMs;

    /// <summary>停顿分位数（毫秒）。没有样本时为 0。</summary>
    public double StallPercentileMs(double percentile)
    {
        if (_stalls.Count == 0)
            return 0d;
        var sorted = _stalls.ToArray();
        Array.Sort(sorted);
        var index = (int)Math.Clamp(Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>是否有可判定的数字（至少两个 tick 才有一个间隔）。</summary>
    public bool HasSamples => _ticks >= 2;

    /// <summary>一句话摘要：没有样本时说"未采样"，绝不把 0 ms 当成"很流畅"。</summary>
    public string Describe(string label = "界面")
    {
        if (!HasSamples)
            return $"{label}停顿：未采样";
        return $"{label}停顿 p95 {StallPercentileMs(0.95):F0} ms · 最大 {MaxStallMs:F0} ms · " +
               $"超过 {_stallThresholdMs:F0} ms 共 {LongStalls} 次（{_ticks} 次采样）";
    }
}

/// <summary>
/// 进度节流（v0.60）：重建索引时每 N 篇就 Report 一次，会向 UI 线程投递成千上万个
/// 消息；用户看到的是"进度条很细致"，实际是 UI 线程在排队。这里按「时间」节流
/// （最近一次真实派发超过最小间隔才放行），最后一条与终态强制放行。
/// <para>
/// 纯逻辑，用调用方给的时间戳判定，所以可以脱离真实时钟写单测。
/// </para>
/// </summary>
public sealed class ProgressThrottle
{
    private readonly double _minimumIntervalMs;
    private long _lastDispatch;
    private bool _dispatched;

    public ProgressThrottle(double minimumIntervalMs = 200d)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumIntervalMs, 0d);
        _minimumIntervalMs = minimumIntervalMs;
    }

    /// <summary>这一次进度要不要真的派发给 <c>IProgress</c>。</summary>
    /// <param name="timestamp">当前时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</param>
    /// <param name="isFinal">终态（100% / 最后一项）必须派发。</param>
    public bool ShouldDispatch(long timestamp, bool isFinal = false)
    {
        if (!isFinal && _dispatched &&
            (timestamp - _lastDispatch) * 1000d / Stopwatch.Frequency < _minimumIntervalMs)
        {
            return false;
        }

        _lastDispatch = timestamp;
        _dispatched = true;
        DispatchCount++;
        return true;
    }

    /// <summary>至今真正派发过多少次；单测与性能报告直接读它。</summary>
    public int DispatchCount { get; private set; }
}

/// <summary>
/// 一次索引重建的实测账本（v0.60）：多久、写了几篇、进度回调派发了几次。
/// <para>
/// 「建立索引时界面卡一段时间」这种反馈没法靠嘴争，只能靠这三个数：
/// 耗时说明任务多大，写入篇数说明磁盘干了多少活，进度派发次数说明把多少工作
/// 压回了 UI 线程。重建前后对比这三个数，改进有没有效一眼就能看出来。
/// </para>
/// </summary>
public sealed record IndexRebuildTelemetry(
    double ElapsedMs,
    int WrittenCount,
    int ProgressDispatchCount,
    int CandidateCount)
{
    public static IndexRebuildTelemetry None { get; } = new(0d, 0, 0, 0);

    public bool HasData => CandidateCount > 0 || ElapsedMs > 0d;

    /// <summary>每分钟处理多少篇：跨机器、跨库大小的可比指标。</summary>
    public double DocumentsPerMinute =>
        ElapsedMs <= 0d ? 0d : CandidateCount * 60_000d / ElapsedMs;

    public string Describe() => HasData
        ? FormattableString.Invariant(
            $"{ElapsedMs:F0} ms · {CandidateCount} 篇（写入 {WrittenCount}）· 进度回调 {ProgressDispatchCount} 次 · {DocumentsPerMinute:F0} 篇/分钟")
        : "本次运行还没有重建记录";
}
