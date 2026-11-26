using System.Text.Json.Serialization;

namespace ClashBarX.Core.Ssid;

/// <summary>SSID 绑定规则（源：Models/SSIDStrategyRule.swift——ssid → 配置文件名）。</summary>
public sealed record SsidStrategyRule(
    [property: JsonPropertyName("ssid")] string Ssid,
    [property: JsonPropertyName("configFileName")] string ConfigFileName)
{
    /// <summary>
    /// 归一化（源：normalized）：trim 两侧、丢弃空字段、同 SSID 后写覆盖前写。
    /// </summary>
    public static IReadOnlyList<SsidStrategyRule> Normalize(IEnumerable<SsidStrategyRule> rules)
    {
        var normalized = new List<SsidStrategyRule>();
        var indexesBySsid = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (SsidStrategyRule rule in rules)
        {
            string ssid = rule.Ssid.Trim();
            string configFileName = rule.ConfigFileName.Trim();
            if (ssid.Length == 0 || configFileName.Length == 0)
            {
                continue;
            }

            var normalizedRule = new SsidStrategyRule(ssid, configFileName);
            if (indexesBySsid.TryGetValue(ssid, out int existingIndex))
            {
                normalized[existingIndex] = normalizedRule;
            }
            else
            {
                indexesBySsid[ssid] = normalized.Count;
                normalized.Add(normalizedRule);
            }
        }

        return normalized;
    }

    /// <summary>插入或覆盖（源：upserting——同 SSID 覆盖）。</summary>
    public static IReadOnlyList<SsidStrategyRule> Upsert(
        string ssid, string configFileName, IEnumerable<SsidStrategyRule> rules)
    {
        if (string.IsNullOrWhiteSpace(ssid) || string.IsNullOrWhiteSpace(configFileName))
        {
            return Normalize(rules);
        }

        return Normalize(rules.Append(new SsidStrategyRule(ssid.Trim(), configFileName.Trim())));
    }

    /// <summary>移除（源：removing）。</summary>
    public static IReadOnlyList<SsidStrategyRule> Remove(
        string ssid, IEnumerable<SsidStrategyRule> rules)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return Normalize(rules);
        }

        string normalizedSsid = ssid.Trim();
        return Normalize(rules).Where(rule => rule.Ssid != normalizedSsid).ToList();
    }
}
