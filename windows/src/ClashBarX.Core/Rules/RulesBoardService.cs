using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Rules;

/// <summary>规则面板快照：规则列表与规则提供者统计（源：ProxyStore 的规则侧字段）。</summary>
public sealed record RulesBoard(
    int RulesCount,
    int ProviderRuleCount,
    IReadOnlyList<RuleItem> Rules,
    IReadOnlyDictionary<string, ProviderDetail> RuleProviders)
{
    public static readonly RulesBoard Empty = new(
        0, 0, Array.Empty<RuleItem>(), new Dictionary<string, ProviderDetail>());
}

/// <summary>
/// 规则面板业务（源：AppViewModel+Providers.swift 的 refreshProvidersAndRules / refreshRuleProviders）：
/// GET /rules + GET /providers/rules 一次写齐；刷新按钮先逐个更新规则集再拉取
/// （provider 不可用不阻断列表展示）。
/// </summary>
public sealed class RulesBoardService
{
    private readonly IMihomoApiClient _client;

    public RulesBoardService(IMihomoApiClient client)
    {
        _client = client;
    }

    /// <summary>拉取规则列表与规则提供者（容错：provider 接口失败不阻断规则列表）。</summary>
    public async Task<RulesBoard> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RulesSummary rules = await _client
            .RequestAsync<RulesSummary>(MihomoEndpoint.Rules(), cancellationToken)
            .ConfigureAwait(false);

        ProviderSummary? providers = null;
        try
        {
            providers = await _client
                .RequestAsync<ProviderSummary>(MihomoEndpoint.RuleProviders(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // provider 接口不可用不阻断规则展示
        }

        IReadOnlyDictionary<string, ProviderDetail> providerLookup =
            providers?.Providers ?? new Dictionary<string, ProviderDetail>();
        return new RulesBoard(
            RulesCount: rules.TotalCount,
            ProviderRuleCount: providerLookup.Count,
            Rules: rules.Rules,
            RuleProviders: providerLookup);
    }

    /// <summary>刷新按钮语义：先逐个更新规则集再重拉（源：refreshRuleProviders 的 PUT 全部再拉）。</summary>
    public async Task<RulesBoard> RefreshProvidersAsync(CancellationToken cancellationToken = default)
    {
        foreach (string name in (await RefreshAsync(cancellationToken).ConfigureAwait(false))
                     .RuleProviders.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            try
            {
                await _client.RequestNoResponseAsync(
                    MihomoEndpoint.UpdateRuleProvider(name), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 单个规则集更新失败不阻断其余（源：逐个容错）
            }
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
