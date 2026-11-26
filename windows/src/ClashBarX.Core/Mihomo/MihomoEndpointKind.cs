namespace ClashBarX.Core.Mihomo;

/// <summary>
/// mihomo External Controller 的 26 个端点种类（源：MihomoAPIService.swift 的 Endpoint 枚举、
/// windows/MIGRATION-GUIDE.md §3 端点表）。种类数量是规格红线，测试会锁定为 26。
/// </summary>
public enum MihomoEndpointKind
{
    // 连通性与四流
    Version,
    Traffic,
    Memory,
    Logs,

    // 配置
    GetConfigs,
    PutConfigs,
    PatchConfigs,

    // 代理与测速
    GroupDelay,
    Proxies,
    ProxyDelay,
    SwitchProxy,

    // 代理订阅源（provider）
    ProxyProviders,
    ProxyProvider,
    UpdateProxyProvider,
    ProxyProviderHealthcheck,
    ProxyProviderProxyHealthcheck,

    // 规则与规则订阅源
    Rules,
    RuleProviders,
    UpdateRuleProvider,

    // 连接
    Connections,
    CloseAllConnections,
    CloseConnection,

    // 维护
    FlushFakeIpCache,
    FlushDnsCache,
    UpgradeCore,
    UpgradeGeo,
}
