using System.Text.Json;
using System.Text.RegularExpressions;
using ClashBarX.Core.Json;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Versioning;

namespace ClashBarX.Core.Maintenance;

/// <summary>内核升级结果形态（源：AppViewModel+Maintenance.swift 的 CoreUpgradeState）。</summary>
public enum CoreUpgradeOutcome
{
    Succeeded,
    AlreadyLatest,
    Failed,
}

/// <summary>
/// 内核升级的归一结果：成功 / 已是最新（可带版本号）/ 失败（带原因）。
/// 判定规则照搬 macOS 侧的 coreUpgradeState(from:)、(from error:)、(fromMessage:)。
/// </summary>
public sealed record CoreUpgradeResult(CoreUpgradeOutcome Outcome, string? Status, string? Version, string? Message)
{
    /// <summary>mihomo 在已是最新版本时回的固定字样（源：isAlreadyLatestCoreUpgradeMessage，忽略大小写）。</summary>
    private const string AlreadyLatestMarker = "already using latest version";

    /// <summary>原因未知时的占位（源：ui.common.unknown）。</summary>
    internal const string UnknownMessage = "--";

    private static readonly Regex VersionPattern = new(@"v?\d+(?:\.\d+)+", RegexOptions.Compiled);

    public static CoreUpgradeResult Succeeded(string? status = null, string? message = null) =>
        new(CoreUpgradeOutcome.Succeeded, status, null, message);

    /// <summary>按 2xx 响应体判定：status 为 ok 即成功，否则看 message。</summary>
    public static CoreUpgradeResult FromResponse(CoreUpgradeResponse? response)
    {
        if (response?.Status is { Length: > 0 } status
            && string.Equals(status.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            return Succeeded(status, response.Message);
        }

        return FromMessage(response?.Message);
    }

    /// <summary>
    /// 按请求失败判定：非 2xx 的响应体若能解出 JSON 先按其 message 判断，解不出再按原文判断——
    /// mihomo 对「已是最新版本」回的是 500 + JSON（真机实测 v1.19.34），应归为「已是最新」而非失败。
    /// </summary>
    public static CoreUpgradeResult FromError(Exception error)
    {
        if (error is MihomoApiException { StatusCode: not null, ResponseBody: { } body })
        {
            if (TryDecode(body, out CoreUpgradeResponse? decoded))
            {
                CoreUpgradeResult fromBody = FromResponse(decoded);
                if (fromBody.Outcome != CoreUpgradeOutcome.Failed || fromBody.Message != UnknownMessage)
                {
                    return fromBody;
                }
            }

            return FromMessage(body);
        }

        return FromMessage(error.Message);
    }

    /// <summary>按一段文字判定：空 = 未知失败；含「already using latest version」= 已是最新；其余 = 失败。</summary>
    public static CoreUpgradeResult FromMessage(string? message)
    {
        string trimmed = message?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return new CoreUpgradeResult(CoreUpgradeOutcome.Failed, null, null, UnknownMessage);
        }

        if (trimmed.Contains(AlreadyLatestMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new CoreUpgradeResult(CoreUpgradeOutcome.AlreadyLatest, null, ExtractVersion(trimmed), trimmed);
        }

        return new CoreUpgradeResult(CoreUpgradeOutcome.Failed, null, null, trimmed);
    }

    /// <summary>文字里最后一个版本号（源：latestVersion(in:)），归一为显示版本；没有则 null。</summary>
    internal static string? ExtractVersion(string message)
    {
        Match? match = VersionPattern.Matches(message).Cast<Match>().LastOrDefault();
        return match is null ? null : AppSemanticVersion.NormalizedDisplayVersion(match.Value);
    }

    private static bool TryDecode(string body, out CoreUpgradeResponse? response)
    {
        try
        {
            response = JsonSerializer.Deserialize<CoreUpgradeResponse>(body, JsonSharedOptions.Json);
            return response is not null;
        }
        catch (JsonException)
        {
            response = null;
            return false;
        }
    }
}

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

    /// <summary>
    /// 更新 GEO 数据（源：upgradeGeo 的 requestNoResponse）。内核对该接口回 204 无响应体，
    /// 2xx 即成功；若按 JSON 解析空响应体，成功也会被误报成失败（真机实测 v1.19.34）。
    /// </summary>
    public Task UpgradeGeoAsync(CancellationToken cancellationToken = default) =>
        _client.RequestNoResponseAsync(MihomoEndpoint.UpgradeGeo(), cancellationToken);

    /// <summary>内核自升级（mihomo /upgrade）；非 2xx 抛异常。</summary>
    public Task<CoreUpgradeResponse> UpgradeCoreAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<CoreUpgradeResponse>(MihomoEndpoint.UpgradeCore(), cancellationToken);

    /// <summary>内核自升级并归一结果：请求失败不抛出，按 macOS 口径转成「已是最新」或「失败（原因）」。</summary>
    public async Task<CoreUpgradeResult> UpgradeCoreWithResultAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return CoreUpgradeResult.FromResponse(await UpgradeCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CoreUpgradeResult.FromError(error);
        }
    }

    /// <summary>清 FakeIP 缓存（源：flushFakeIPCache）。</summary>
    public Task FlushFakeIPCacheAsync(CancellationToken cancellationToken = default) =>
        _client.RequestNoResponseAsync(MihomoEndpoint.FlushFakeIpCache(), cancellationToken);

    /// <summary>清 DNS 缓存（源：flushDNSCache）。</summary>
    public Task FlushDNSCacheAsync(CancellationToken cancellationToken = default) =>
        _client.RequestNoResponseAsync(MihomoEndpoint.FlushDnsCache(), cancellationToken);
}
