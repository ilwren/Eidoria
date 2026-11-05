namespace MdExplorer.Application;

/// <summary>拖放与文件传输的纯规则（不依赖文件系统与 UI，供 VM 与测试共用）。</summary>
public static class FileTransferRules
{
    /// <summary>
    /// 拖放默认动作：Ctrl 恒为复制；Shift 恒为移动；无修饰键时应用内默认移动、应用外默认复制。
    /// </summary>
    public static bool ShouldMove(bool isInternalDrag, bool ctrlPressed, bool shiftPressed) =>
        !ctrlPressed && (shiftPressed || isInternalDrag);

    /// <summary>
    /// candidate 是否等于 ancestor 或位于 ancestor 的后代链上。父链由调用方提供，
    /// 可统一处理本地路径与 storage URI。防御异常父链循环：最多向上追溯 1024 层。
    /// </summary>
    public static bool IsSameOrDescendant(
        string candidate,
        string ancestor,
        Func<string, string?> getParent,
        Func<string, string, bool> pathsEqual)
    {
        string? current = candidate;
        for (var depth = 0; current is not null && depth < 1024; depth++)
        {
            if (pathsEqual(current, ancestor))
                return true;
            current = getParent(current);
        }
        return false;
    }
}
