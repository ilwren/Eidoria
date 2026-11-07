using Android.App;
using Android.Content;
using Android.Hardware;
using Android.OS;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Android;

/// <summary>
/// 折叠屏姿态来源（v0.58）：读 Android 的**铰链角度传感器**（<c>TYPE_HINGE_ANGLE</c>，API 30 起），
/// 换算成 <see cref="FoldablePosture"/> 交给界面。
/// <para>
/// 为什么用传感器而不是 <c>androidx.window</c>：后者能同时报「铰链走向 + 位置」，但要再拉一个
/// Java 绑定依赖；铰链角度传感器是系统自带的，零依赖就能判出「是否半折叠」。
/// 走向则按窗口横竖推断（横窗=上下两半、竖窗=左右两半），判错只是主副两半换方向，
/// 不会出现内容被折进去——任何姿态下主区都只占第一半。
/// </para>
/// <para>
/// 老实说清楚的边界：没有铰链传感器的设备（绝大多数手机、桌面）拿到的永远是
/// <see cref="FoldablePosture.Flat"/>，界面与旧版一模一样；铰链有多宽传感器不报，
/// 这里给保守留白 <see cref="FoldableLayout.DefaultGutterDip"/>。
/// </para>
/// </summary>
public sealed class AndroidFoldablePostureSource : Java.Lang.Object, ISensorEventListener, IFoldablePostureSource
{
    /// <summary><c>android.hardware.Sensor.TYPE_HINGE_ANGLE</c> 的原始值（API 30 起）。</summary>
    private const int HingeAngleSensorType = 36;

    private readonly Activity _activity;
    private readonly SensorManager? _sensors;
    private readonly Sensor? _hinge;
    private float _lastAngle = 180;
    private bool _registered;

    public AndroidFoldablePostureSource(Activity activity)
    {
        _activity = activity;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return;

        try
        {
            _sensors = activity.GetSystemService(Context.SensorService) as SensorManager;
            // Sensor.TYPE_HINGE_ANGLE = 36（API 30）。绑定里那个 int 常量已标 obsolete，
            // 而 SensorType 枚举未见 HingeAngle 成员，这里按原值强转，语义与系统常量一一对应。
            _hinge = _sensors?.GetDefaultSensor((SensorType)HingeAngleSensorType);
            if (_sensors is not null && _hinge is not null)
                _registered = _sensors.RegisterListener(this, _hinge, SensorDelay.Normal);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            // 传感器是锦上添花：拿不到就当平铺，绝不让界面起不来。
            _registered = false;
        }
    }

    /// <summary>这台设备有没有可用的铰链角度传感器（没有就一直是平铺）。</summary>
    public bool IsSupported => _registered;

    public FoldablePosture Current { get; private set; } = FoldablePosture.Flat;

    public event EventHandler? Changed;

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
    {
        // 铰链角度不需要精度处理：区间判定本身就是粗粒度的。
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        var values = e?.Values;
        if (values is null || values.Count == 0)
            return;

        _lastAngle = values[0];
        Update();
    }

    /// <summary>窗口横竖可能变了（旋转、分屏），重新算一次走向。</summary>
    public void Refresh()
    {
        Update();
    }

    private void Update()
    {
        var next = FoldableLayout.FromHingeAngle(_lastAngle, IsLandscape());
        if (next.Equals(Current))
            return;
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsLandscape()
    {
        try
        {
            var metrics = _activity.Resources?.DisplayMetrics;
            if (metrics is null || metrics.WidthPixels <= 0 || metrics.HeightPixels <= 0)
                return false;
            return metrics.WidthPixels >= metrics.HeightPixels;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Java.Lang.Exception)
        {
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _registered && _sensors is not null)
        {
            try
            {
                _sensors.UnregisterListener(this);
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or ObjectDisposedException)
            {
            }

            _registered = false;
        }

        base.Dispose(disposing);
    }
}
