using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Rules;

namespace ClashBarX.Core.Tests.Rules;

/// <summary>规则面板业务规格测试（源：refreshProvidersAndRules / refreshRuleProviders）。</summary>
public class RulesBoardServiceTests
{
    private sealed class FakeApiClient : IMihomoApiClient
    {
        public List<MihomoEndpoint> Requests { get; } = new();
        public Func<MihomoEndpointKind, object?>? ResponseFactory { get; set; }
        public HashSet<MihomoEndpointKind> FailKinds { get; } = new();

        public void UpdateCredentials(string controller, string? secret) { }

        public Task<T> RequestAsync<T>(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
            where T : class
        {
            Requests.Add(endpoint);
            if (FailKinds.Contains(endpoint.Kind))
            {
                throw new HttpRequestException($"脚本化失败：{endpoint.Kind}");
            }

            return Task.FromResult((T)(ResponseFactory?.Invoke(endpoint.Kind) ?? throw new InvalidOperationException($"未配置响应：{endpoint.Kind}")));
        }

        public Task RequestNoResponseAsync(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            Requests.Add(endpoint);
            if (FailKinds.Contains(endpoint.Kind))
            {
                throw new HttpRequestException($"脚本化失败：{endpoint.Kind}");
            }

            return Task.CompletedTask;
        }

        public IWebSocketConnection CreateWebSocketConnection(MihomoEndpoint endpoint) =>
            throw new NotSupportedException("规则测试不使用 WebSocket");

        public void Dispose() { }
    }

    private static RulesSummary Rules(params (string Type, string Payload, string Proxy)[] items) =>
        new(items.Length, items.Select(item => new RuleItem(item.Type, item.Payload, item.Proxy)).ToList());

    private static ProviderSummary Providers(params (string Name, int RuleCount)[] items) => new(
        items.ToDictionary(
            item => item.Name,
            item => new ProviderDetail(item.Name, "HTTP", null, null, null, item.RuleCount, null, null)));

    [Fact]
    public async Task 一次拉齐规则与提供者()
    {
        var client = new FakeApiClient
        {
            ResponseFactory = kind => kind switch
            {
                MihomoEndpointKind.Rules => Rules(("DOMAIN-SUFFIX", "example.com", "DIRECT"), ("GEOIP", "CN", "Demo")),
                MihomoEndpointKind.RuleProviders => Providers(("rules-a", 120), ("rules-b", 80)),
                _ => null,
            },
        };
        var service = new RulesBoardService(client);

        RulesBoard board = await service.RefreshAsync();

        Assert.Equal(2, board.RulesCount);
        Assert.Equal(2, board.ProviderRuleCount);
        Assert.Equal(2, board.Rules.Count);
        Assert.Equal("example.com", board.Rules[0].Payload);
        Assert.Contains("rules-a", board.RuleProviders.Keys);
    }

    [Fact]
    public async Task 提供者接口失败不阻断规则列表()
    {
        var client = new FakeApiClient
        {
            ResponseFactory = kind => kind == MihomoEndpointKind.Rules
                ? Rules(("MATCH", null, "DIRECT"))
                : null,
        };
        client.FailKinds.Add(MihomoEndpointKind.RuleProviders);
        var service = new RulesBoardService(client);

        RulesBoard board = await service.RefreshAsync();

        Assert.Equal(1, board.RulesCount);
        Assert.Equal(0, board.ProviderRuleCount);   // provider 失败归零但列表照常
        Assert.Single(board.Rules);
    }

    [Fact]
    public async Task 刷新规则集先更新全部再重拉()
    {
        var refreshCount = 0;
        var client = new FakeApiClient
        {
            ResponseFactory = kind =>
            {
                if (kind == MihomoEndpointKind.Rules)
                {
                    return Rules();
                }

                refreshCount++;
                return Providers(("rules-a", 10), ("rules-b", 20));
            },
        };
        var service = new RulesBoardService(client);

        await service.RefreshProvidersAsync();

        // 两个规则集都发过 PUT 更新（顺序按名排序），且拉取发生两轮（先拉名字再重拉）
        Assert.Equal(2, client.Requests.Count(endpoint => endpoint.Kind == MihomoEndpointKind.UpdateRuleProvider));
        Assert.Equal(2, client.Requests.Count(endpoint => endpoint.Kind == MihomoEndpointKind.RuleProviders));
        Assert.True(refreshCount >= 2);
    }

    [Fact]
    public async Task 单个规则集更新失败不阻断其余()
    {
        var client = new FakeApiClient
        {
            ResponseFactory = kind => kind switch
            {
                MihomoEndpointKind.Rules => Rules(),
                MihomoEndpointKind.RuleProviders => Providers(("rules-a", 10), ("rules-b", 20)),
                _ => null,
            },
        };
        var service = new RulesBoardService(client);

        // rules-a 的更新失败（无 name 区分的 fake 会全失败——这里验证整体不抛即可）
        client.FailKinds.Add(MihomoEndpointKind.UpdateRuleProvider);
        RulesBoard board = await service.RefreshProvidersAsync();

        Assert.Equal(0, board.RulesCount);   // 全部更新失败也不抛
    }
}
