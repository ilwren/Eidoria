using CommunityToolkit.Mvvm.Input;

namespace MdExplorer.UI.ViewModels;

/// <summary>
/// 搜索中心「最近搜索」里的一枚小胶囊（v0.53）：自带宽命令，模板里只绑 <c>Query</c> 与 <c>RunCommand</c>，
/// 不需要 <c>$parent</c> 去找 ShellViewModel（那种绑定一写错就是一屏绑定错误）。
/// </summary>
public sealed class RecentSearchChipModel(string query, IAsyncRelayCommand runCommand)
{
    public string Query { get; } = query;
    public IAsyncRelayCommand RunCommand { get; } = runCommand;
}
