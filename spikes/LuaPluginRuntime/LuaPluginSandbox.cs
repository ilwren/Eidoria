using System.Reflection;
using MoonSharp.Interpreter;

namespace MdExplorer.LuaPluginRuntime;

/// <summary>A demo result is always a preview: this spike has no file-system or notification capability.</summary>
public sealed record LuaDemoResult(string PreviewText, int InputFileCount, bool RequiresUserConfirmation, bool WroteFiles);

public sealed record LuaPluginDemo(string Id, string Name, string Description, string ResourceName);

/// <summary>
/// Isolated MoonSharp spike for testing bounded, data-only Lua plans. Do not wire into the product
/// until the Native AOT, Trim, Android, and security gates in docs/LUA-PLUGIN-SPIKE.md pass.
/// </summary>
public static class LuaPluginSandbox
{
    public const int MaximumSourceLength = 16_384;
    public const int MaximumInputFiles = 100_000;
    public const int InstructionsPerResume = 2_000;
    public const int MaximumResumes = 64;

    private static readonly LuaPluginDemo[] DemoDefinitions =
    [
        new("organize-images", "图片整理", "按输入数量生成一个只读复制计划预览。", "OrganizeImages.lua"),
        new("rename-preview", "序号重命名预览", "展示重命名计划，不改动任何文件。", "RenamePreview.lua"),
        new("inventory-report", "文件清单", "生成只读清单计划，不创建输出文件。", "InventoryReport.lua"),
    ];

    public static IReadOnlyList<LuaPluginDemo> Demos => DemoDefinitions;

    public static LuaDemoResult RunDemo(string id, int inputFileCount)
    {
        var demo = DemoDefinitions.FirstOrDefault(item => item.Id == id)
                   ?? throw new ArgumentException("未知的 Lua 示例。", nameof(id));
        var source = LoadSource(demo);
        return Execute(source, inputFileCount);
    }

    public static LuaDemoResult Execute(string source, int inputFileCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0 || source.Length > MaximumSourceLength)
            throw new ArgumentOutOfRangeException(nameof(source), $"Lua 源码必须为 1–{MaximumSourceLength} 个字符。");
        if (inputFileCount is < 0 or > MaximumInputFiles)
            throw new ArgumentOutOfRangeException(nameof(inputFileCount));

        // Preset_HardSandbox excludes io, os, debug, load/require and CLR interop modules.
        // Only two primitive globals are exposed; neither contains a path or a host object.
        var script = new Script(CoreModules.Preset_HardSandbox);
        script.Globals.Set("eidoria_file_count", DynValue.NewNumber(inputFileCount));
        script.Globals.Set("eidoria_preview", DynValue.NewCallback((_, args) =>
            CreatePreview(args, inputFileCount)));

        var entryPoint = script.LoadString(source);
        var coroutine = script.CreateCoroutine(entryPoint);
        coroutine.Coroutine.AutoYieldCounter = InstructionsPerResume;

        var result = coroutine.Coroutine.Resume();
        var resumeCount = 0;
        while (result.Type == DataType.YieldRequest)
        {
            if (++resumeCount > MaximumResumes)
                throw new TimeoutException("Lua 示例超过指令预算，已停止。");
            result = coroutine.Coroutine.Resume();
        }

        if (result.Type != DataType.String)
            throw new InvalidDataException("Lua 插件必须返回宿主生成的预览文本；不能返回可执行对象或任意 CLR 值。");

        return new LuaDemoResult(result.String, inputFileCount, RequiresUserConfirmation: true, WroteFiles: false);
    }

    private static DynValue CreatePreview(CallbackArguments args, int inputFileCount)
    {
        if (args.Count != 3 || args[0].Type != DataType.String ||
            args[1].Type != DataType.String || args[2].Type != DataType.String)
            throw new ScriptRuntimeException("eidoria_preview(title, action, target) 只接受三个字符串。");

        var title = args[0].String.Trim();
        var action = args[1].String.Trim();
        var target = args[2].String.Trim();
        if (title.Length is 0 or > 80 || target.Length is 0 or > 160 ||
            title.Any(char.IsControl) || target.Any(char.IsControl))
            throw new ScriptRuntimeException("Lua 预览字段为空、过长或含控制字符。");
        if (action is not ("copy-preview" or "rename-preview" or "report-only"))
            throw new ScriptRuntimeException("Lua 预览动作不在允许清单中。");

        var actionLabel = action switch
        {
            "copy-preview" => "复制计划",
            "rename-preview" => "重命名计划",
            _ => "只读报告",
        };
        return DynValue.NewString(
            $"{title}：{actionLabel} → {target}；模拟输入 {inputFileCount} 项；等待宿主确认，未写入文件。");
    }

    private static string LoadSource(LuaPluginDemo demo)
    {
        var resourceName = $"{typeof(LuaPluginSandbox).Namespace}.Samples.{demo.ResourceName}";
        using var stream = typeof(LuaPluginSandbox).Assembly.GetManifestResourceStream(resourceName)
                          ?? throw new InvalidOperationException($"Lua 示例资源缺失：{resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
