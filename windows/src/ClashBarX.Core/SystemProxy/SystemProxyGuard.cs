namespace ClashBarX.Core.SystemProxy;

/// <summary>
/// 系统代理守护比较逻辑（源：systemProxyStillMatchesExpectedTarget / checkSystemProxyConfiguration）。
/// 判定「当前系统值是否仍匹配期望目标」：只有存在开启意图且不匹配时才需要回写，
/// 无意图时被外部改动不越权回写。
/// </summary>
public static class SystemProxyGuard
{
    /// <summary>当前快照是否与期望目标完全一致（enabled + server + 绕过列表）。</summary>
    public static bool Matches(SystemProxySnapshot snapshot, SystemProxyTarget expected)
    {
        if (!snapshot.Enabled)
        {
            return false;
        }

        if (!string.Equals(
                NormalizeHost(snapshot.Server),
                NormalizeHost(expected.ServerDisplay),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<string> actual = SystemProxyBypass.Parse(RenderedOverride(snapshot));
        IReadOnlyList<string> expectedList = SystemProxyBypass.Normalize(expected.BypassList);
        return actual.Count == expectedList.Count
            && actual.SequenceEqual(expectedList, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>是否存在开启意图且系统值偏离期望（需要守护回写）。</summary>
    public static bool NeedsRewrite(SystemProxySnapshot snapshot, SystemProxyTarget? expected)
    {
        return expected != null && !Matches(snapshot, expected);
    }

    /// <summary>环回地址归一化（源：normalizedSystemProxyComparisonHost 的 loopback 折叠）。</summary>
    internal static string? NormalizeHost(string? server)
    {
        string? value = server?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // ProxyServer 形如 host:port / host；仅归一 host 部分
        int colon = value.LastIndexOf(':');
        string host = colon > 0 ? value[..colon] : value;
        string port = colon > 0 ? value[colon..] : string.Empty;
        return host switch
        {
            "localhost" or "127.0.0.1" or "0.0.0.0" or "::1" or "::" or "0:0:0:0:0:0:0:0" => "loopback" + port,
            _ => host.ToLowerInvariant() + port,
        };
    }

    private static string? RenderedOverride(SystemProxySnapshot snapshot) =>
        snapshot.BypassList.Count == 0 ? null : string.Join(";", snapshot.BypassList);
}
