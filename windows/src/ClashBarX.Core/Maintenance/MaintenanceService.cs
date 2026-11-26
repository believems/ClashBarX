using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Maintenance;

/// <summary>
/// 维护动作（源：AppViewModel+Maintenance.swift 的维护区）：
/// GEO 数据更新（内核热更新 geodata，Windows 可用）与内核升级（mihomo 自升级 API；
/// Windows 上运行中 exe 可能被锁，失败由调用方如实反馈）。
/// </summary>
public sealed class MaintenanceService
{
    private readonly IMihomoApiClient _client;

    public MaintenanceService(IMihomoApiClient client)
    {
        _client = client;
    }

    /// <summary>更新 GEO 数据（geosite/geoip）。</summary>
    public Task<CoreUpgradeResponse> UpgradeGeoAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<CoreUpgradeResponse>(MihomoEndpoint.UpgradeGeo(), cancellationToken);

    /// <summary>内核自升级（mihomo /upgrade）。</summary>
    public Task<CoreUpgradeResponse> UpgradeCoreAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<CoreUpgradeResponse>(MihomoEndpoint.UpgradeCore(), cancellationToken);
}
