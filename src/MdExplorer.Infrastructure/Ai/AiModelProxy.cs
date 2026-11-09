using System.Net;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>
/// AI 模型下载的代理配方（v0.61）。
/// <para>
/// 三档语义固定，任何非法输入都落回「跟随系统」而不是悄悄直连——用户网络环境里
/// 「能不能连上」比「配置长什么样」更重要，手改设置文件改了也别让下载莫名断网。
/// </para>
/// </summary>
public sealed record AiProxyPlan(bool UseProxy, string? Address)
{
    /// <summary>跟随系统：交给 SocketsHttpHandler 的默认代理发现。</summary>
    public static readonly AiProxyPlan System = new(UseProxy: true, Address: null);

    /// <summary>直连：显式不走任何代理。</summary>
    public static readonly AiProxyPlan Direct = new(UseProxy: false, Address: null);

    /// <summary>把设置里的两个字段翻译成配方；自定义地址无效时退回跟随系统。</summary>
    public static AiProxyPlan Resolve(AppSettings settings)
    {
        var mode = NormalizeMode(settings.AiModelProxyMode);
        if (mode == "None")
            return Direct;
        if (mode != "Custom")
            return System;

        var address = settings.AiModelProxyAddress.Trim();
        if (address.Length == 0)
            return System;
        // 允许只填「host:port」：补 http:// 前缀后再校验，免得用户因为一个斜杠被挡在门外。
        if (!address.Contains("://", StringComparison.Ordinal))
            address = "http://" + address;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new AiProxyPlan(UseProxy: true, Address: uri.AbsoluteUri)
            : System;
    }

    /// <summary>把任意输入收口到 System / None / Custom。</summary>
    public static string NormalizeMode(string? mode) => mode?.Trim() switch
    {
        "None" => "None",
        "Custom" => "Custom",
        _ => "System",
    };

    /// <summary>按需取 <see cref="IWebProxy"/>；直连与跟随系统都返回 null（由 handler 标志位区分）。</summary>
    public IWebProxy? CreateWebProxy() => UseProxy && Address is { Length: > 0 } address
        ? new WebProxy(new Uri(address, UriKind.Absolute))
        : null;

    /// <summary>缓存键：同一份配方复用同一个 HttpClient，换档才重建。</summary>
    public string CacheKey => UseProxy ? "proxy:" + (Address ?? "system") : "direct";
}
