using MdExplorer.LuaPluginRuntime;
using MoonSharp.Interpreter;
using Xunit;

namespace MdExplorer.LuaRuntime.Tests;

public sealed class LuaPluginSandboxTests
{
    [Theory]
    [InlineData("organize-images", "复制计划")]
    [InlineData("rename-preview", "重命名计划")]
    [InlineData("inventory-report", "只读报告")]
    public void BuiltInLuaDemos_ReturnReviewablePreview_WithoutSideEffects(string id, string expectedAction)
    {
        var result = LuaPluginSandbox.RunDemo(id, inputFileCount: 3);

        Assert.Contains(expectedAction, result.PreviewText, StringComparison.Ordinal);
        Assert.Contains("模拟输入 3 项", result.PreviewText, StringComparison.Ordinal);
        Assert.True(result.RequiresUserConfirmation);
        Assert.False(result.WroteFiles);
    }

    [Fact]
    public void HardSandbox_DoesNotExposeOperatingSystemOrFileIo()
    {
        Assert.Throws<ScriptRuntimeException>(() =>
            LuaPluginSandbox.Execute("return os.execute('touch /tmp/unsafe')", inputFileCount: 1));
        Assert.Throws<ScriptRuntimeException>(() =>
            LuaPluginSandbox.Execute("return io.open('/tmp/unsafe', 'w')", inputFileCount: 1));
    }

    [Fact]
    public void InfiniteLoop_StopsAtInstructionBudget()
    {
        Assert.Throws<TimeoutException>(() =>
            LuaPluginSandbox.Execute("while true do end", inputFileCount: 0));
    }

    [Fact]
    public void PreviewApi_RejectsUnknownActionsAndOversizedInputs()
    {
        Assert.Throws<ScriptRuntimeException>(() =>
            LuaPluginSandbox.Execute(
                "return eidoria_preview('unsafe', 'delete', 'all')",
                inputFileCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LuaPluginSandbox.Execute("return 'ok'", inputFileCount: LuaPluginSandbox.MaximumInputFiles + 1));
    }
}
