namespace ClashBarX.Core.Ssid;

public enum SsidStrategyResolutionKind
{
    NoAction,

    /// <summary>命中的规则指向已不存在的配置文件（源：missingConfig）。</summary>
    MissingConfig,

    /// <summary>需要切换到规则指定的配置（源：switchToConfig）。</summary>
    SwitchToConfig,
}

/// <summary>解析结果（源：SSIDStrategyResolution）。</summary>
public sealed record SsidStrategyResolution(SsidStrategyResolutionKind Kind, string? ConfigFileName)
{
    public static SsidStrategyResolution NoAction { get; } = new(SsidStrategyResolutionKind.NoAction, null);

    public static SsidStrategyResolution MissingConfig(string configFileName) =>
        new(SsidStrategyResolutionKind.MissingConfig, configFileName);

    public static SsidStrategyResolution SwitchTo(string configFileName) =>
        new(SsidStrategyResolutionKind.SwitchToConfig, configFileName);
}

/// <summary>
/// SSID 策略解析（源：resolveSSIDStrategyConfig）：SSID 无绑定或当前已是目标配置 → NoAction；
/// 命中但目标配置不在可用列表 → MissingConfig；否则 → SwitchToConfig。
/// </summary>
public static class SsidStrategyResolver
{
    public static SsidStrategyResolution Resolve(
        string? currentSsid,
        string? currentConfigName,
        IEnumerable<SsidStrategyRule> rules,
        IReadOnlyCollection<string> availableConfigNames)
    {
        if (string.IsNullOrWhiteSpace(currentSsid))
        {
            return SsidStrategyResolution.NoAction;
        }

        string normalizedSsid = currentSsid.Trim();
        SsidStrategyRule? matchedRule = SsidStrategyRule.Normalize(rules)
            .FirstOrDefault(rule => rule.Ssid == normalizedSsid);
        if (matchedRule is null)
        {
            return SsidStrategyResolution.NoAction;
        }

        string targetConfigName = matchedRule.ConfigFileName;
        if (!availableConfigNames.Any(name => name.Trim() == targetConfigName))
        {
            return SsidStrategyResolution.MissingConfig(targetConfigName);
        }

        if (targetConfigName == currentConfigName?.Trim())
        {
            return SsidStrategyResolution.NoAction;
        }

        return SsidStrategyResolution.SwitchTo(targetConfigName);
    }
}
