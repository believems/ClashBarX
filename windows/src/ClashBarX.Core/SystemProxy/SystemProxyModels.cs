namespace ClashBarX.Core.SystemProxy;

/// <summary>期望的系统代理目标（源：SystemProxyTarget；Windows 用单一混合端口承载 http+https）。</summary>
public sealed record SystemProxyTarget(string Host, int Port, IReadOnlyList<string> BypassList)
{
    public string ServerDisplay => Port > 0 ? $"{Host}:{Port}" : Host;

    // 数组字段需要值语义相等（record 默认对 IReadOnlyList 按引用比较）
    public bool Equals(SystemProxyTarget? other) =>
        other is not null
        && Host == other.Host
        && Port == other.Port
        && BypassList.SequenceEqual(other.BypassList);

    public override int GetHashCode() =>
        HashCode.Combine(Host, Port);
}

/// <summary>系统代理注册表读值快照。</summary>
public sealed record SystemProxySnapshot(
    bool Enabled,
    string? Server,
    IReadOnlyList<string> BypassList)
{
    public static readonly SystemProxySnapshot Disabled = new(false, null, Array.Empty<string>());
}

/// <summary>
/// 系统代理约定值。默认绕过列表与 macOS 版同意图（源：defaultSystemProxyExceptions），
/// CIDR 段按 Windows ProxyOverride 的通配语法适配（172.16.0.0/12 近似为 172.*）。
/// </summary>
public static class SystemProxyDefaults
{
    /// <summary>默认绕过列表：环回、本地名、三段私网。</summary>
    public static readonly IReadOnlyList<string> DefaultBypassList = new[]
    {
        "localhost",
        "127.*",
        "::1",
        "*.local",
        "<local>",
        "192.168.*",
        "10.*",
        "172.*",
    };

    /// <summary>绕过列表序列化为 ProxyOverride 值（分号分隔）。</summary>
    public static string RenderOverride(IReadOnlyList<string> bypassList) =>
        string.Join(";", bypassList);
}

public static class SystemProxyBypass
{
    /// <summary>
    /// 归一化绕过列表：trim、去空、大小写不敏感去重、保持首次出现顺序
    /// （源：normalizedSystemProxyExceptionValues）。
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result;
    }

    /// <summary>把注册表 ProxyOverride 值拆回列表（容忍空项与空白）。</summary>
    public static IReadOnlyList<string> Parse(string? proxyOverride) =>
        Normalize((proxyOverride ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries));
}
