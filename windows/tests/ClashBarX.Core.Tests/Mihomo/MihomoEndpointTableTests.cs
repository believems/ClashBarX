using System.Text;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Mihomo;

/// <summary>
/// 26 端点表驱动规格测试（源：MIGRATION-GUIDE.md §3 端点表 + MihomoAPIService.swift）。
/// 与指南表格的三处出入按「以 Swift 为准」红线处理并在行内注明：
/// 1. rules 超时为 5s（指南表格写 2s，Swift 源码为 5s）；
/// 2. flush 两端点无请求体（指南表格写同 upgrade 的空 body，Swift 源码无 body）；
/// 3. connections 的 interval 单位为毫秒（指南表格写秒，Swift 调用与 mihomo 源码均为毫秒）。
/// </summary>
public class MihomoEndpointTableTests
{
    public static TheoryData<string, Func<MihomoEndpoint>, string, string, string[], string?, double, bool> EndpointSpecs { get; } =
        new()
        {
            // 描述, 端点工厂, method, path, query(有序 key=value), body JSON, 超时(秒), 长会话
            { "version", MihomoEndpoint.Version, "GET", "/version", Array.Empty<string>(), null, 2, false },
            { "traffic", MihomoEndpoint.Traffic, "GET", "/traffic", Array.Empty<string>(), null, 2, false },
            { "memory", MihomoEndpoint.Memory, "GET", "/memory", Array.Empty<string>(), null, 2, false },
            { "logs(带级别)", () => MihomoEndpoint.Logs("info"), "GET", "/logs", new[] { "level=info" }, null, 2, false },
            { "logs(无级别)", () => MihomoEndpoint.Logs(null), "GET", "/logs", Array.Empty<string>(), null, 2, false },
            { "logs(空串级别)", () => MihomoEndpoint.Logs(""), "GET", "/logs", Array.Empty<string>(), null, 2, false },
            { "getConfigs", MihomoEndpoint.GetConfigs, "GET", "/configs", Array.Empty<string>(), null, 5, false },
            {
                "putConfigs(force, path)",
                () => MihomoEndpoint.PutConfigs(force: true, path: "/tmp/a.yaml", payload: null),
                "PUT", "/configs", new[] { "force=true" }, "{\"path\":\"/tmp/a.yaml\"}", 30, true
            },
            {
                "putConfigs(无 force, payload 优先)",
                () => MihomoEndpoint.PutConfigs(force: false, path: "/tmp/b.yaml", payload: "mode: rule"),
                "PUT", "/configs", Array.Empty<string>(), "{\"payload\":\"mode: rule\"}", 30, true
            },
            {
                "patchConfigs",
                () => MihomoEndpoint.PatchConfigs(new System.Text.Json.Nodes.JsonObject
                {
                    ["mode"] = "global",
                    ["allow-lan"] = true,
                }),
                "PATCH", "/configs", Array.Empty<string>(), "{\"mode\":\"global\",\"allow-lan\":true}", 5, false
            },
            {
                "groupDelay(中文组名)",
                () => MihomoEndpoint.GroupDelay("中国", "https://www.gstatic.com/generate_204", 5000),
                "GET", "/group/%E4%B8%AD%E5%9B%BD/delay",
                new[] { "url=https://www.gstatic.com/generate_204", "timeout=5000" },
                null, 7, true
            },
            { "proxies", MihomoEndpoint.Proxies, "GET", "/proxies", Array.Empty<string>(), null, 5, false },
            {
                "proxyDelay(emoji 节点名)",
                () => MihomoEndpoint.ProxyDelay("🇭🇰 node", "https://www.gstatic.com/generate_204", 5000),
                "GET", "/proxies/%F0%9F%87%AD%F0%9F%87%B0%20node/delay",
                new[] { "url=https://www.gstatic.com/generate_204", "timeout=5000" },
                null, 7, true
            },
            {
                "switchProxy",
                () => MihomoEndpoint.SwitchProxy("选择组", "香港 01"),
                "PUT", "/proxies/%E9%80%89%E6%8B%A9%E7%BB%84", Array.Empty<string>(),
                "{\"name\":\"香港 01\"}", 10, false
            },
            { "proxyProviders", MihomoEndpoint.ProxyProviders, "GET", "/providers/proxies", Array.Empty<string>(), null, 5, false },
            { "proxyProvider", () => MihomoEndpoint.ProxyProvider("sub1"), "GET", "/providers/proxies/sub1", Array.Empty<string>(), null, 5, false },
            {
                "updateProxyProvider",
                () => MihomoEndpoint.UpdateProxyProvider("sub1"),
                "PUT", "/providers/proxies/sub1", Array.Empty<string>(), null, 30, true
            },
            {
                "proxyProviderHealthcheck(恒 180s)",
                () => MihomoEndpoint.ProxyProviderHealthcheck("sub1", "https://www.gstatic.com/generate_204", 5000),
                "GET", "/providers/proxies/sub1/healthcheck",
                new[] { "url=https://www.gstatic.com/generate_204", "timeout=5000" },
                null, 180, true
            },
            {
                "proxyProviderProxyHealthcheck",
                () => MihomoEndpoint.ProxyProviderProxyHealthcheck("sub1", "🇯🇵", "https://www.gstatic.com/generate_204", 5000),
                "GET", "/providers/proxies/sub1/%F0%9F%87%AF%F0%9F%87%B5/healthcheck",
                new[] { "url=https://www.gstatic.com/generate_204", "timeout=5000" },
                null, 7, true
            },
            // 与指南表格出入 1：rules 超时以 Swift 源码为准（5s，非 2s）
            { "rules", MihomoEndpoint.Rules, "GET", "/rules", Array.Empty<string>(), null, 5, false },
            { "ruleProviders", MihomoEndpoint.RuleProviders, "GET", "/providers/rules", Array.Empty<string>(), null, 5, false },
            {
                "updateRuleProvider",
                () => MihomoEndpoint.UpdateRuleProvider("ruleset1"),
                "PUT", "/providers/rules/ruleset1", Array.Empty<string>(), null, 30, true
            },
            // 与指南表格出入 3：interval 单位为毫秒
            { "connections(无 interval)", () => MihomoEndpoint.Connections(null), "GET", "/connections", Array.Empty<string>(), null, 2, false },
            { "connections(1000ms)", () => MihomoEndpoint.Connections(1000), "GET", "/connections", new[] { "interval=1000" }, null, 2, false },
            { "closeAllConnections", MihomoEndpoint.CloseAllConnections, "DELETE", "/connections", Array.Empty<string>(), null, 2, false },
            { "closeConnection", () => MihomoEndpoint.CloseConnection("abc123"), "DELETE", "/connections/abc123", Array.Empty<string>(), null, 2, false },
            // 与指南表格出入 2：flush 两端点无请求体（对齐 Swift 源码）
            { "flushFakeIPCache", MihomoEndpoint.FlushFakeIpCache, "POST", "/cache/fakeip/flush", Array.Empty<string>(), null, 2, false },
            { "flushDNSCache", MihomoEndpoint.FlushDnsCache, "POST", "/cache/dns/flush", Array.Empty<string>(), null, 2, false },
            { "upgradeCore", MihomoEndpoint.UpgradeCore, "POST", "/upgrade", Array.Empty<string>(), "{\"path\":\"\",\"payload\":\"\"}", 60, true },
            { "upgradeGeo", MihomoEndpoint.UpgradeGeo, "POST", "/upgrade/geo", Array.Empty<string>(), "{\"path\":\"\",\"payload\":\"\"}", 60, true },
        };

    [Fact]
    public void 端点种类恰好为26个()
    {
        Assert.Equal(26, Enum.GetValues<MihomoEndpointKind>().Length);
    }

    [Fact]
    public void 规格表覆盖全部26个端点种类()
    {
        var covered = new HashSet<MihomoEndpointKind>();
        foreach (var row in EndpointSpecs)
        {
            var factory = (Func<MihomoEndpoint>)row[1]!;
            covered.Add(factory().Kind);
        }

        var missing = Enum.GetValues<MihomoEndpointKind>().Where(kind => !covered.Contains(kind)).ToList();
        Assert.True(missing.Count == 0, $"规格表未覆盖：{string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(EndpointSpecs))]
    public void 端点属性与规格表一致(
        string description,
        Func<MihomoEndpoint> factory,
        string method,
        string path,
        string[] query,
        string? bodyJson,
        double timeoutSeconds,
        bool usesLongRunningSession)
    {
        MihomoEndpoint endpoint = factory();

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Equal(method, endpoint.Method.ToHttpString());
        Assert.Equal(path, endpoint.Path);
        Assert.Equal(query, endpoint.QueryItems.Select(pair => $"{pair.Key}={pair.Value}").ToArray());
        Assert.Equal(bodyJson, endpoint.Body == null ? null : Encoding.UTF8.GetString(endpoint.Body));
        Assert.Equal(TimeSpan.FromSeconds(timeoutSeconds), endpoint.Timeout);
        Assert.Equal(usesLongRunningSession, endpoint.UsesLongRunningSession);
    }

    public static TheoryData<Func<MihomoEndpoint>, int, double> DynamicDelayTimeouts { get; } = new()
    {
        // 测速超时 = max(5, timeout/1000 + 2) 秒
        { CreateGroupDelay(1000), 1000, 5 },
        { CreateGroupDelay(3000), 3000, 5 },
        { CreateGroupDelay(5000), 5000, 7 },
        { CreateGroupDelay(8000), 8000, 10 },
        { () => MihomoEndpoint.ProxyDelay("n", "u", 3000), 3000, 5 },
        { () => MihomoEndpoint.ProxyDelay("n", "u", 9000), 9000, 11 },
        { () => MihomoEndpoint.ProxyProviderProxyHealthcheck("p", "n", "u", 12000), 12000, 14 },
    };

    [Theory]
    [MemberData(nameof(DynamicDelayTimeouts))]
    public void 测速端点超时按公式计算(Func<MihomoEndpoint> factory, int timeoutMilliseconds, double expectedSeconds)
    {
        MihomoEndpoint endpoint = factory();
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), endpoint.Timeout);
        Assert.Contains(
            $"timeout={timeoutMilliseconds}",
            endpoint.QueryItems.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static Func<MihomoEndpoint> CreateGroupDelay(int timeoutMilliseconds) =>
        () => MihomoEndpoint.GroupDelay("g", "u", timeoutMilliseconds);
}
