using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Proxies;

/// <summary>代理面板快照：排序后的分组、节点类型、延迟历史与 provider 词典。</summary>
public sealed record ProxyBoard(
    IReadOnlyList<ProxyGroup> Groups,
    IReadOnlyDictionary<string, string> NodeTypes,
    IReadOnlyDictionary<string, IReadOnlyList<int>> DelaySamples,
    IReadOnlyDictionary<string, ProviderDetail> Providers)
{
    public static readonly ProxyBoard Empty = new(
        Array.Empty<ProxyGroup>(),
        new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<int>>(),
        new Dictionary<string, ProviderDetail>());
}

/// <summary>
/// 代理面板业务（源：AppViewModel+Polling.swift 的 applyProxyGroupsResponse 排序合并、
/// AppViewModel+Proxy.swift 的切换与测速流程）。
/// 分组排序：GLOBAL.all 的成员序优先（GLOBAL 自身垫尾），其余按名称不区分大小写；
/// 分组的测速参数合并 provider 的 testUrl/timeout；单节点测速按「provider 节点走
/// provider healthcheck，否则 proxyDelay」预判。
/// </summary>
public sealed class ProxyBoardService
{
    /// <summary>测速参数缺省（源：defaultHealthcheckURL / defaultHealthcheckTimeoutMilliseconds）。</summary>
    public const string DefaultHealthcheckUrl = "https://www.gstatic.com/generate_204";
    public const int DefaultHealthcheckTimeoutMilliseconds = 5000;

    private static readonly string[] DelayTestFailureStatusHints = { "503", "504" };

    private readonly IMihomoApiClient _client;

    public ProxyBoardService(IMihomoApiClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 刷新面板：GET /proxies + GET /providers/proxies（容错，provider 不可用不阻断），
    /// 合并 provider 的测速参数与节点类型/延迟历史后排序。
    /// </summary>
    public async Task<ProxyBoard> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ProxyGroupsResponse response = await _client
            .RequestAsync<ProxyGroupsResponse>(MihomoEndpoint.Proxies(), cancellationToken)
            .ConfigureAwait(false);

        ProviderSummary? providers = null;
        try
        {
            providers = await _client
                .RequestAsync<ProviderSummary>(MihomoEndpoint.ProxyProviders(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // provider 接口不可用不阻断分组展示（源：fetchProxyProvidersSummarySafely）
        }

        return BuildBoard(response, providers);
    }

    /// <summary>切换分组当前节点（源：switchProxy——PUT 后由上层刷新分组同步实际选中）。</summary>
    public Task SwitchNodeAsync(string groupName, string targetNode, CancellationToken cancellationToken = default) =>
        _client.RequestNoResponseAsync(
            MihomoEndpoint.SwitchProxy(groupName, targetNode), cancellationToken);

    /// <summary>更新单个 provider（源：updateProxyProvider——PUT /providers/proxies/{name}）。</summary>
    public Task UpdateProviderAsync(string providerName, CancellationToken cancellationToken = default) =>
        _client.RequestNoResponseAsync(
            MihomoEndpoint.UpdateProxyProvider(providerName), cancellationToken);

    /// <summary>更新全部 provider（源：refreshAllProxyProviders——逐个更新，逐个容错）。</summary>
    public async Task<IReadOnlyList<string>> UpdateAllProvidersAsync(CancellationToken cancellationToken = default)
    {
        ProviderSummary? providers = null;
        try
        {
            providers = await _client
                .RequestAsync<ProviderSummary>(MihomoEndpoint.ProxyProviders(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Array.Empty<string>();   // 列表拉取失败视为无 provider（源：静默容错）
        }

        var updated = new List<string>();
        foreach (string name in providers?.Providers.Keys ?? Enumerable.Empty<string>())
        {
            try
            {
                await UpdateProviderAsync(name, cancellationToken).ConfigureAwait(false);
                updated.Add(name);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 单个失败不阻断其余（源：逐个容错）
            }
        }

        return updated;
    }

    /// <summary>单个 provider 全量 healthcheck（源：providerActionButton(.healthcheck)——
    /// GET /providers/proxies/{name}/healthcheck，测速参数取 provider 声明或缺省）。</summary>
    public async Task HealthcheckProviderAsync(string providerName, CancellationToken cancellationToken = default)
    {
        (string url, int timeout) = await ResolveProviderHealthcheckParametersAsync(providerName, cancellationToken)
            .ConfigureAwait(false);
        await _client.RequestNoResponseAsync(
            MihomoEndpoint.ProxyProviderHealthcheck(providerName, url, timeout), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(string Url, int Timeout)> ResolveProviderHealthcheckParametersAsync(
        string providerName, CancellationToken cancellationToken)
    {
        try
        {
            ProviderSummary? providers = await _client
                .RequestAsync<ProviderSummary>(MihomoEndpoint.ProxyProviders(), cancellationToken)
                .ConfigureAwait(false);
            if (providers?.Providers.TryGetValue(providerName, out ProviderDetail? provider) == true
                && provider != null)
            {
                return (provider.TestUrl ?? DefaultHealthcheckUrl, provider.Timeout ?? DefaultHealthcheckTimeoutMilliseconds);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 参数解析失败走缺省
        }

        return (DefaultHealthcheckUrl, DefaultHealthcheckTimeoutMilliseconds);
    }

    /// <summary>
    /// 组测速：返回节点名 → 延迟毫秒。未测到的成员按 0（超时）记录
    /// （源：groupDelayResult 的 members 全集语义）。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> TestGroupAsync(
        string groupName, CancellationToken cancellationToken = default)
    {
        ProxyBoard board = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        ProxyGroup? group = board.Groups.FirstOrDefault(item => item.Name == groupName);
        if (group is null)
        {
            throw new InvalidOperationException($"proxy group not found: {groupName}");
        }

        (string url, int timeout) = ResolveHealthcheckParameters(group);
        try
        {
            GroupDelayMeasurement measurement = await _client
                .RequestAsync<GroupDelayMeasurement>(
                    MihomoEndpoint.GroupDelay(groupName, url, timeout), cancellationToken)
                .ConfigureAwait(false);
            var measured = measurement.Values;
            return group.All.ToDictionary(
                member => member,
                member => measured.TryGetValue(member, out int delay) ? delay : 0);
        }
        catch (MihomoApiException error) when (error.StatusCode is int code
            && DelayTestFailureStatusHints.Any(hint => code.ToString() == hint))
        {
            // 全员失败：全部记 0（源：delayTestFailureStatusCodes 分支）
            return group.All.ToDictionary(member => member, _ => 0);
        }
    }

    /// <summary>
    /// 单节点测速：节点来自订阅 provider 时走 provider 的单节点 healthcheck，
    /// 否则走 /proxies/{name}/delay（源：AppViewModel+Proxy.swift 的 provider 预判）。
    /// 返回延迟毫秒（null = 超时/不可用）。
    /// </summary>
    public async Task<int?> TestNodeAsync(
        string groupName, string nodeName, CancellationToken cancellationToken = default)
    {
        ProxyBoard board = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        ProxyGroup? group = board.Groups.FirstOrDefault(item => item.Name == groupName);
        if (group is null)
        {
            throw new InvalidOperationException($"proxy group not found: {groupName}");
        }

        (string url, int timeout) = ResolveHealthcheckParameters(group);
        string? providerName = FindProviderContaining(board, nodeName);
        MihomoEndpoint endpoint = providerName is null
            ? MihomoEndpoint.ProxyDelay(nodeName, url, timeout)
            : MihomoEndpoint.ProxyProviderProxyHealthcheck(providerName, nodeName, url, timeout);

        try
        {
            DelayMeasurement measurement = await _client
                .RequestAsync<DelayMeasurement>(endpoint, cancellationToken)
                .ConfigureAwait(false);
            return measurement.Value;
        }
        catch (MihomoApiException error) when (error.StatusCode is int code
            && DelayTestFailureStatusHints.Any(hint => code.ToString() == hint))
        {
            return null;   // 节点测速失败记为超时（源：测速失败记 0 的语义）
        }
    }

    internal static ProxyBoard BuildBoard(ProxyGroupsResponse response, ProviderSummary? providers)
    {
        IReadOnlyDictionary<string, ProviderDetail> providerLookup =
            providers?.Providers ?? new Dictionary<string, ProviderDetail>();

        // 合并 provider 的测速参数（源：applyProxyGroupsResponse 的 proxiesWithHealthcheckConfig）
        var groupsWithConfig = response.Proxies.Values
            .Select(proxy =>
            {
                ProviderDetail? provider = providerLookup.GetValueOrDefault(proxy.Name);
                return proxy with
                {
                    TestUrl = proxy.TestUrl ?? provider?.TestUrl,
                    Timeout = proxy.Timeout ?? provider?.Timeout,
                };
            })
            .Where(proxy => proxy.All.Count > 0)   // 只有分组（all 非空）
            .ToList();

        // 排序：GLOBAL 成员序优先，GLOBAL 自身垫尾，其余按名称不区分大小写
        IReadOnlyList<string> sortIndex = response.Proxies.TryGetValue("GLOBAL", out ProxyGroup? global) && global != null
            ? global.All.Append("GLOBAL").ToList()
            : new[] { "GLOBAL" };
        var orderLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in sortIndex)
        {
            orderLookup.TryAdd(name, orderLookup.Count);
        }

        List<ProxyGroup> groups = groupsWithConfig
            .OrderBy(group => orderLookup.TryGetValue(group.Name, out int order) ? order : int.MaxValue)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 节点类型（all 为空的是节点）与延迟历史（含 provider 节点）
        var nodeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var delaySamples = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (ProxyGroup proxy in response.Proxies.Values)
        {
            if (proxy.All.Count == 0 && proxy.Type is { } type)
            {
                nodeTypes[proxy.Name] = type;
            }

            if (proxy.DelayHistory.Count > 0)
            {
                delaySamples[proxy.Name] = proxy.DelayHistory;
            }
        }

        foreach (ProviderDetail provider in providerLookup.Values)
        {
            if (provider.Proxies is not { } nodes)
            {
                continue;
            }

            foreach (ProviderProxyNode node in nodes)
            {
                if (node.DelayHistory.Count > 0 && !delaySamples.ContainsKey(node.Name))
                {
                    delaySamples[node.Name] = node.DelayHistory;
                }

                if (node.Type is { } type && !nodeTypes.ContainsKey(node.Name))
                {
                    nodeTypes[node.Name] = type;
                }
            }
        }

        return new ProxyBoard(groups, nodeTypes, delaySamples, providerLookup);
    }

    private static (string Url, int Timeout) ResolveHealthcheckParameters(ProxyGroup group) =>
        (group.TestUrl ?? DefaultHealthcheckUrl, group.Timeout ?? DefaultHealthcheckTimeoutMilliseconds);

    /// <summary>节点所属的订阅 provider 名（无则走普通 proxyDelay；源：subscriptionProvider(containing:)）。</summary>
    private static string? FindProviderContaining(ProxyBoard board, string nodeName)
    {
        foreach (KeyValuePair<string, ProviderDetail> pair in board.Providers)
        {
            if (pair.Value.Proxies?.Any(node => node.Name == nodeName) == true)
            {
                return pair.Key;
            }
        }

        return null;
    }
}
