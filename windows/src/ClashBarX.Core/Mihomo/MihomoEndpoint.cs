using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Mihomo;

/// <summary>
/// 一次 mihomo API 调用的完整描述：方法、路径、查询、请求体、超时与会话档位
/// （源：MihomoAPIService.swift 的 Endpoint 枚举）。所有路径参数在构造时即完成百分号转义。
/// </summary>
public sealed class MihomoEndpoint
{
    private const string ProxyProvidersPath = "/providers/proxies";
    private const string RuleProvidersPath = "/providers/rules";

    // upgrade 端点的固定请求体（源：Endpoint.coreUpgradeRequestBody）
    private static readonly byte[] CoreUpgradeRequestBody =
        "{\"path\":\"\",\"payload\":\"\"}"u8.ToArray();

    // 端点参数载体：不同端点只用到其中的子集
    private sealed record Arguments(
        string? Name = null,
        string? Target = null,
        string? Provider = null,
        string? Proxy = null,
        string? Url = null,
        int TimeoutMilliseconds = 0,
        int? IntervalMilliseconds = null,
        string? Level = null,
        bool Force = false,
        string? ConfigPath = null,
        string? ConfigPayload = null,
        JsonObject? PatchBody = null);

    private readonly Arguments _arguments;

    private MihomoEndpoint(MihomoEndpointKind kind, Arguments arguments)
    {
        Kind = kind;
        _arguments = arguments;
        Path = BuildPath();
        QueryItems = BuildQuery();
        Body = BuildBody();
        Timeout = BuildTimeout();
    }

    public MihomoEndpointKind Kind { get; }

    public MihomoHttpMethod Method => Kind switch
    {
        MihomoEndpointKind.Version or MihomoEndpointKind.Traffic or MihomoEndpointKind.Memory
            or MihomoEndpointKind.Logs or MihomoEndpointKind.GetConfigs or MihomoEndpointKind.GroupDelay
            or MihomoEndpointKind.Proxies or MihomoEndpointKind.ProxyDelay or MihomoEndpointKind.ProxyProviders
            or MihomoEndpointKind.ProxyProvider or MihomoEndpointKind.ProxyProviderHealthcheck
            or MihomoEndpointKind.ProxyProviderProxyHealthcheck or MihomoEndpointKind.Rules
            or MihomoEndpointKind.RuleProviders or MihomoEndpointKind.Connections => MihomoHttpMethod.Get,

        MihomoEndpointKind.PutConfigs or MihomoEndpointKind.SwitchProxy
            or MihomoEndpointKind.UpdateProxyProvider or MihomoEndpointKind.UpdateRuleProvider => MihomoHttpMethod.Put,

        MihomoEndpointKind.PatchConfigs => MihomoHttpMethod.Patch,

        MihomoEndpointKind.CloseAllConnections or MihomoEndpointKind.CloseConnection => MihomoHttpMethod.Delete,

        MihomoEndpointKind.FlushFakeIpCache or MihomoEndpointKind.FlushDnsCache
            or MihomoEndpointKind.UpgradeCore or MihomoEndpointKind.UpgradeGeo => MihomoHttpMethod.Post,

        _ => throw new InvalidOperationException($"unknown endpoint kind: {Kind}"),
    };

    /// <summary>已转义的请求路径（如 /group/%E4%B8%AD/delay）。</summary>
    public string Path { get; }

    /// <summary>查询键值对（值未转义，由 UrlBuilder 统一编码）。顺序即发送顺序。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> QueryItems { get; }

    /// <summary>已序列化的 JSON 请求体；无 body 的端点为 null。注意 flush 两端点无 body（对齐 Swift 源码）。</summary>
    public byte[]? Body { get; }

    /// <summary>请求超时（源：Endpoint.timeoutInterval）。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>是否走长耗时会话（连接数上限与超时档位更宽，源：Endpoint.usesLongRunningSession）。</summary>
    public bool UsesLongRunningSession => Kind is MihomoEndpointKind.GroupDelay
        or MihomoEndpointKind.ProxyDelay
        or MihomoEndpointKind.ProxyProviderHealthcheck
        or MihomoEndpointKind.ProxyProviderProxyHealthcheck
        or MihomoEndpointKind.PutConfigs
        or MihomoEndpointKind.UpdateProxyProvider
        or MihomoEndpointKind.UpdateRuleProvider
        or MihomoEndpointKind.UpgradeCore
        or MihomoEndpointKind.UpgradeGeo;

    // ---- 工厂（26 个，签名对齐 Swift Endpoint 的 case） ----

    public static MihomoEndpoint Version() => new(MihomoEndpointKind.Version, new());
    public static MihomoEndpoint Traffic() => new(MihomoEndpointKind.Traffic, new());
    public static MihomoEndpoint Memory() => new(MihomoEndpointKind.Memory, new());
    public static MihomoEndpoint Logs(string? level) => new(MihomoEndpointKind.Logs, new(Level: level));
    public static MihomoEndpoint GetConfigs() => new(MihomoEndpointKind.GetConfigs, new());

    public static MihomoEndpoint PutConfigs(bool force, string path, string? payload) =>
        new(MihomoEndpointKind.PutConfigs, new(Force: force, ConfigPath: path, ConfigPayload: payload));

    public static MihomoEndpoint PatchConfigs(JsonObject body) =>
        new(MihomoEndpointKind.PatchConfigs, new(PatchBody: body));

    public static MihomoEndpoint GroupDelay(string name, string url, int timeoutMilliseconds) =>
        new(MihomoEndpointKind.GroupDelay, new(Name: name, Url: url, TimeoutMilliseconds: timeoutMilliseconds));

    public static MihomoEndpoint Proxies() => new(MihomoEndpointKind.Proxies, new());

    public static MihomoEndpoint ProxyDelay(string name, string url, int timeoutMilliseconds) =>
        new(MihomoEndpointKind.ProxyDelay, new(Name: name, Url: url, TimeoutMilliseconds: timeoutMilliseconds));

    public static MihomoEndpoint SwitchProxy(string name, string target) =>
        new(MihomoEndpointKind.SwitchProxy, new(Name: name, Target: target));

    public static MihomoEndpoint ProxyProviders() => new(MihomoEndpointKind.ProxyProviders, new());
    public static MihomoEndpoint ProxyProvider(string name) => new(MihomoEndpointKind.ProxyProvider, new(Name: name));
    public static MihomoEndpoint UpdateProxyProvider(string name) =>
        new(MihomoEndpointKind.UpdateProxyProvider, new(Name: name));

    public static MihomoEndpoint ProxyProviderHealthcheck(string name, string url, int timeoutMilliseconds) =>
        new(MihomoEndpointKind.ProxyProviderHealthcheck, new(Name: name, Url: url, TimeoutMilliseconds: timeoutMilliseconds));

    public static MihomoEndpoint ProxyProviderProxyHealthcheck(string provider, string proxy, string url, int timeoutMilliseconds) =>
        new(MihomoEndpointKind.ProxyProviderProxyHealthcheck,
            new(Provider: provider, Proxy: proxy, Url: url, TimeoutMilliseconds: timeoutMilliseconds));

    public static MihomoEndpoint Rules() => new(MihomoEndpointKind.Rules, new());
    public static MihomoEndpoint RuleProviders() => new(MihomoEndpointKind.RuleProviders, new());
    public static MihomoEndpoint UpdateRuleProvider(string name) =>
        new(MihomoEndpointKind.UpdateRuleProvider, new(Name: name));

    public static MihomoEndpoint Connections(int? intervalMilliseconds) =>
        new(MihomoEndpointKind.Connections, new(IntervalMilliseconds: intervalMilliseconds));

    public static MihomoEndpoint CloseAllConnections() => new(MihomoEndpointKind.CloseAllConnections, new());
    public static MihomoEndpoint CloseConnection(string id) =>
        new(MihomoEndpointKind.CloseConnection, new(Name: id));
    public static MihomoEndpoint FlushFakeIpCache() => new(MihomoEndpointKind.FlushFakeIpCache, new());
    public static MihomoEndpoint FlushDnsCache() => new(MihomoEndpointKind.FlushDnsCache, new());
    public static MihomoEndpoint UpgradeCore() => new(MihomoEndpointKind.UpgradeCore, new());
    public static MihomoEndpoint UpgradeGeo() => new(MihomoEndpointKind.UpgradeGeo, new());

    // ---- 计算属性 ----

    private string BuildPath() => Kind switch
    {
        MihomoEndpointKind.Version => "/version",
        MihomoEndpointKind.Traffic => "/traffic",
        MihomoEndpointKind.Memory => "/memory",
        MihomoEndpointKind.Logs => "/logs",
        MihomoEndpointKind.GetConfigs or MihomoEndpointKind.PutConfigs or MihomoEndpointKind.PatchConfigs => "/configs",
        MihomoEndpointKind.GroupDelay => $"/group/{MihomoUrlBuilder.EscapePathSegment(_arguments.Name!)}/delay",
        MihomoEndpointKind.Proxies => "/proxies",
        MihomoEndpointKind.ProxyDelay => $"/proxies/{MihomoUrlBuilder.EscapePathSegment(_arguments.Name!)}/delay",
        MihomoEndpointKind.SwitchProxy => $"/proxies/{MihomoUrlBuilder.EscapePathSegment(_arguments.Name!)}",
        MihomoEndpointKind.ProxyProviders => ProxyProvidersPath,
        MihomoEndpointKind.ProxyProvider or MihomoEndpointKind.UpdateProxyProvider => ProxyProviderPath(_arguments.Name!),
        MihomoEndpointKind.ProxyProviderHealthcheck => $"{ProxyProviderPath(_arguments.Name!)}/healthcheck",
        MihomoEndpointKind.ProxyProviderProxyHealthcheck =>
            $"{ProxyProviderPath(_arguments.Provider!)}/{MihomoUrlBuilder.EscapePathSegment(_arguments.Proxy!)}/healthcheck",
        MihomoEndpointKind.Rules => "/rules",
        MihomoEndpointKind.RuleProviders => RuleProvidersPath,
        MihomoEndpointKind.UpdateRuleProvider =>
            $"{RuleProvidersPath}/{MihomoUrlBuilder.EscapePathSegment(_arguments.Name!)}",
        MihomoEndpointKind.Connections or MihomoEndpointKind.CloseAllConnections => "/connections",
        MihomoEndpointKind.CloseConnection => $"/connections/{MihomoUrlBuilder.EscapePathSegment(_arguments.Name!)}",
        MihomoEndpointKind.FlushFakeIpCache => "/cache/fakeip/flush",
        MihomoEndpointKind.FlushDnsCache => "/cache/dns/flush",
        MihomoEndpointKind.UpgradeCore => "/upgrade",
        MihomoEndpointKind.UpgradeGeo => "/upgrade/geo",
        _ => throw new InvalidOperationException($"unknown endpoint kind: {Kind}"),
    };

    private IReadOnlyList<KeyValuePair<string, string>> BuildQuery()
    {
        switch (Kind)
        {
            case MihomoEndpointKind.Logs:
                return OptionalQueryItem("level", _arguments.Level);

            case MihomoEndpointKind.PutConfigs:
                return _arguments.Force
                    ? new[] { new KeyValuePair<string, string>("force", "true") }
                    : Array.Empty<KeyValuePair<string, string>>();

            case MihomoEndpointKind.GroupDelay:
            case MihomoEndpointKind.ProxyDelay:
            case MihomoEndpointKind.ProxyProviderHealthcheck:
            case MihomoEndpointKind.ProxyProviderProxyHealthcheck:
                return new[]
                {
                    new KeyValuePair<string, string>("url", _arguments.Url ?? string.Empty),
                    new KeyValuePair<string, string>("timeout", _arguments.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)),
                };

            case MihomoEndpointKind.Connections:
                return OptionalQueryItem("interval", _arguments.IntervalMilliseconds?.ToString(CultureInfo.InvariantCulture));

            default:
                return Array.Empty<KeyValuePair<string, string>>();
        }
    }

    private byte[]? BuildBody() => Kind switch
    {
        // payload 存在时优先（源：Endpoint.body 的 putConfigs 分支）
        MihomoEndpointKind.PutConfigs => _arguments.ConfigPayload is not null
            ? SerializeJson(new { payload = _arguments.ConfigPayload })
            : SerializeJson(new { path = _arguments.ConfigPath }),

        MihomoEndpointKind.PatchConfigs => SerializeJsonNode(_arguments.PatchBody!),
        MihomoEndpointKind.SwitchProxy => SerializeJson(new { name = _arguments.Target }),
        MihomoEndpointKind.UpgradeCore or MihomoEndpointKind.UpgradeGeo => CoreUpgradeRequestBody,
        _ => null,
    };

    private TimeSpan BuildTimeout() => Kind switch
    {
        MihomoEndpointKind.ProxyProviderHealthcheck => TimeSpan.FromSeconds(180),

        MihomoEndpointKind.GroupDelay or MihomoEndpointKind.ProxyDelay or MihomoEndpointKind.ProxyProviderProxyHealthcheck =>
            // max(5, timeout/1000 + 2) 秒（源：Endpoint.timeoutInterval 的测速分支）
            TimeSpan.FromSeconds(Math.Max(5.0, _arguments.TimeoutMilliseconds / 1000.0 + 2)),

        MihomoEndpointKind.UpgradeCore or MihomoEndpointKind.UpgradeGeo => TimeSpan.FromSeconds(60),

        MihomoEndpointKind.SwitchProxy => TimeSpan.FromSeconds(10),

        MihomoEndpointKind.PutConfigs or MihomoEndpointKind.UpdateProxyProvider or MihomoEndpointKind.UpdateRuleProvider =>
            TimeSpan.FromSeconds(30),

        MihomoEndpointKind.Proxies or MihomoEndpointKind.ProxyProviders or MihomoEndpointKind.ProxyProvider
            or MihomoEndpointKind.RuleProviders or MihomoEndpointKind.Rules or MihomoEndpointKind.GetConfigs
            or MihomoEndpointKind.PatchConfigs => TimeSpan.FromSeconds(5),

        _ => TimeSpan.FromSeconds(2),
    };

    private static string ProxyProviderPath(string name) =>
        $"{ProxyProvidersPath}/{MihomoUrlBuilder.EscapePathSegment(name)}";

    private static IReadOnlyList<KeyValuePair<string, string>> OptionalQueryItem(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        return new[] { new KeyValuePair<string, string>(name, value) };
    }

    private static byte[] SerializeJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonSharedOptions.Json);

    private static byte[] SerializeJsonNode(JsonObject body) =>
        JsonSerializer.SerializeToUtf8Bytes(body, JsonSharedOptions.Json);
}
