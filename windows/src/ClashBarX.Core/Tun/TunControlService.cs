using System.Text.Json.Nodes;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Versioning;

namespace ClashBarX.Core.Tun;

/// <summary>TUN 运行态（源：AppViewModel+Tun.swift 从 GET /configs 提取的 tun.enable/tun.stack）。</summary>
public sealed record TunRuntimeState(bool Enabled, string? Stack);

public enum TunToggleOutcome
{
    /// <summary>PATCH 后运行态与期望一致（源：actualState == enabled 分支）。</summary>
    Applied,

    /// <summary>运行态与期望不一致（源：TunModeError.runtimeStateMismatch → log error，不抛）。</summary>
    RuntimeStateMismatch,
}

public sealed record TunToggleResult(TunToggleOutcome Outcome, TunRuntimeState State);

/// <summary>
/// TUN 业务（源：AppViewModel+Tun.swift）：PATCH → GET 验证的开关语义、
/// stack 选择、启动恢复（开关校验与 stack 对齐）。权限语义（macOS setuid /
/// Windows 管理员）不在本层——由 App 层在调用前完成提权判定。
/// </summary>
public sealed class TunControlService
{
    /// <summary>mips stack 的内核版本门槛（源：mipsStackMinimumCoreVersion）。</summary>
    public const string MipsStackMinimumCoreVersion = "1.19.31";

    /// <summary>stack 选项（源：ProxyTabView.tunStackOptions；mips 受版本门槛约束）。</summary>
    public static readonly string[] StackOptions = { "system", "gvisor", "mixed", "mips" };

    /// <summary>无保存值且配置未声明 stack 时的缺省（源：preferredTunStack 的 "mixed" 兜底）。</summary>
    public const string DefaultStack = "mixed";

    private readonly IMihomoApiClient _client;

    public TunControlService(IMihomoApiClient client)
    {
        _client = client;
    }

    /// <summary>当前内核版本是否支持 mips stack（源：isMipsStackSupported；版本不可解析时 false）。</summary>
    public static bool IsMipsStackSupported(string? coreVersion) =>
        AppSemanticVersion.Parse(coreVersion) is { } current
        && AppSemanticVersion.Parse(MipsStackMinimumCoreVersion) is { } minimum
        && current >= minimum;

    /// <summary>GET /configs 提取 TUN 运行态（enable 缺省 false；stack 取 trim 非空）。</summary>
    public async Task<TunRuntimeState> FetchRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        ConfigSnapshot snapshot = await _client
            .RequestAsync<ConfigSnapshot>(MihomoEndpoint.GetConfigs(), cancellationToken)
            .ConfigureAwait(false);
        return new TunRuntimeState(
            snapshot.TunEnabled ?? false,
            string.IsNullOrWhiteSpace(snapshot.Tun?.Stack) ? null : snapshot.Tun!.Stack!.Trim());
    }

    /// <summary>
    /// 开关切换（源：toggleTunMode 的 patchTunConfig → 验证链）：
    /// PATCH {"tun":{"enable":..,"stack"?}} 后 GET 验证实际态。
    /// stack 仅在 enable 且非空时注入；PATCH 异常上抛（调用方反馈错误并回读状态）。
    /// </summary>
    public async Task<TunToggleResult> ToggleAsync(bool enable, string? stack = null, CancellationToken cancellationToken = default)
    {
        await PatchTunAsync(enable, stack, cancellationToken).ConfigureAwait(false);
        TunRuntimeState state = await FetchRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        return new TunToggleResult(
            state.Enabled == enable ? TunToggleOutcome.Applied : TunToggleOutcome.RuntimeStateMismatch,
            state);
    }

    /// <summary>选择 stack（源：selectTunStack——以 enable=true + stack 打补丁后验证）。</summary>
    public Task<TunToggleResult> SelectStackAsync(string stack, CancellationToken cancellationToken = default) =>
        ToggleAsync(enable: true, stack, cancellationToken);

    /// <summary>
    /// 启动恢复（源：verifyTunAfterOverlayIfNeeded）：运行态已开直接确认；
    /// 未开则 PATCH enable + preferred stack 后验证。结果交调用方决定
    /// 成功保持开关 / 失败回落（Windows 权限失败路径对应源 validateTunPermissionsOnStartup）。
    /// </summary>
    public async Task<TunToggleResult> RestoreAtStartupAsync(bool savedEnabled, string? preferredStack, CancellationToken cancellationToken = default)
    {
        if (!savedEnabled)
        {
            TunRuntimeState state = await FetchRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
            return new TunToggleResult(TunToggleOutcome.Applied, state);
        }

        TunRuntimeState current = await FetchRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        if (current.Enabled)
        {
            return new TunToggleResult(TunToggleOutcome.Applied, current);
        }

        return await ToggleAsync(enable: true, preferredStack, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动 stack 对齐（源：ensureTunStackOnStartupIfNeeded）：TUN 开启且 runtime stack
    /// 与偏好不同时 PATCH {"tun":{"stack":..}}（不动 enable）。异常静默由调用方日志
    /// （源：catch 后 log error）。返回是否发生了对齐。
    /// </summary>
    public async Task<bool> AlignStackIfEnabledAsync(string? preferredStack, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preferredStack))
        {
            return false;
        }

        TunRuntimeState state = await FetchRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Enabled || string.Equals(state.Stack, preferredStack, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await PatchTunAsync(enable: null, preferredStack, cancellationToken).ConfigureAwait(false);
        _ = await FetchRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>PATCH tun 块（源：patchTunConfig）：enable 与 stack 按需注入。</summary>
    private async Task PatchTunAsync(bool? enable, string? stack, CancellationToken cancellationToken)
    {
        var tunBody = new JsonObject();
        if (enable is { } enableValue)
        {
            tunBody["enable"] = enableValue;
        }

        if (enable != false && !string.IsNullOrWhiteSpace(stack))
        {
            tunBody["stack"] = stack;
        }

        await _client.RequestNoResponseAsync(
            MihomoEndpoint.PatchConfigs(new JsonObject { ["tun"] = tunBody }),
            cancellationToken).ConfigureAwait(false);
    }
}
