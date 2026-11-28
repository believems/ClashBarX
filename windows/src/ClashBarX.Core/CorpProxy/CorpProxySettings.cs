namespace ClashBarX.Core.CorpProxy;

/// <summary>
/// 内网前置代理设置（Issue #2，源：macOS CorpProxySettings）：集团内网仅允许经企业 HTTP 代理
/// 出外网时，节点连接与订阅拉取统一经该代理拨出（mihomo dialer-proxy 语义）。
/// 端口缺省 8080 对齐 Mac 的 @AppStorage 缺省。
/// </summary>
public sealed record CorpProxySettings(
    bool IsEnabled,
    string Host,
    int Port,
    string Username,
    string Password)
{
    public static readonly CorpProxySettings Disabled = new(false, string.Empty, 8080, string.Empty, string.Empty);

    public string TrimmedHost => Host?.Trim() ?? string.Empty;

    public bool HasCredentials =>
        !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password);

    /// <summary>地址与端口就绪（不含 IsEnabled 判定）。</summary>
    public bool IsValid =>
        TrimmedHost.Length > 0 && Port is >= 1 and <= 65535;

    /// <summary>开关打开且配置可用。</summary>
    public bool IsReady => IsEnabled && IsValid;
}
