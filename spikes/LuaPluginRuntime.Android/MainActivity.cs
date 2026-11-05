using Android.App;
using Android.OS;
using Android.Widget;
using Android.Util;
using MdExplorer.LuaPluginRuntime;

namespace MdExplorer.LuaPluginRuntime.AndroidSpike;

[Activity(Label = "Lua plugin runtime spike", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            var result = LuaPluginSandbox.RunDemo("organize-images", inputFileCount: 3);
            Log.Info("LuaRuntimeSpike", $"PASS: {result.PreviewText}; wrote-files={result.WroteFiles}");
            SetContentView(new TextView(this)
            {
                Text = $"MoonSharp Android spike\n\n{result.PreviewText}\n\n" +
                       $"Needs confirmation: {result.RequiresUserConfirmation}\nFiles written: {result.WroteFiles}",
                TextSize = 20,
            });
        }
        catch (Exception exception)
        {
            Log.Error("LuaRuntimeSpike", $"FAIL: {exception}");
            SetContentView(new TextView(this)
            {
                Text = $"MoonSharp Android spike failed\n\n{exception}",
                TextSize = 18,
            });
        }
    }
}
