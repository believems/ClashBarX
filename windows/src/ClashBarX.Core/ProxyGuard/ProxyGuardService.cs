using ClashBarX.Core.Mihomo;
using ClashBarX.Core.L10n;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.Core.ProxyGuard;

/// <summary>最近一次探测结果（源：ProxyGuardProbeOutcome——供设置页反馈行显示）。</summary>
public enum ProxyGuardProbeOutcome
{
    Reachable,
    Failed,
}

public sealed record ProxyGuardProbeState(ProxyGuardProbeOutcome Outcome, int ConsecutiveFailures);

/// <summary>
/// 自动代理守护（源：AppViewModel+ProxyGuard.swift 逐行对照）。
/// 周期性经当前混合端口探测外网连通性，连续 3 次失败后按「组内切换 → 订阅自愈」恢复：
/// 组内切换 = 对每个 Selector 组测速，当前节点不可用而组内有可用节点时切换到最快；
/// 全部组无可用节点时更新全部代理提供者（订阅自愈），3 秒后重测再切换。
/// 探测/延迟/组测速均注入可测；事件回调来自后台线程，UI 侧自行编组。
/// </summary>
public sealed class ProxyGuardService : IDisposable
{
    public const int FailureThreshold = 3;
    public const string DefaultProbeUrl = "https://www.gstatic.com/generate_204";
    public const int MinimumIntervalSeconds = 30;
    public const int DefaultHealthcheckTimeoutMilliseconds = 5000;

    /// <summary>CLI 触发参数（源：cliTriggerArgument = "--proxy-guard"）。</summary>
    public const string CliTriggerArgument = "--proxy-guard";

    /// <summary>订阅自愈后等待内核刷新的时间（源：3_000_000_000ns）。</summary>
    public static readonly TimeSpan ProviderRefreshWait = TimeSpan.FromSeconds(3);

    private readonly IMihomoApiClient _client;
    private readonly Func<CancellationToken, Task<bool>> _probeAsync;
    private readonly Func<int> _intervalSecondsProvider;
    private readonly Func<bool> _canProbe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _gate = new();
    /// <summary>探测串行化门（源：Swift 的 MainActor——立即检查与循环探测不并发）。</summary>
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private int _consecutiveFailures;
    private bool _recoveryInFlight;

    public ProxyGuardService(
        IMihomoApiClient client,
        Func<CancellationToken, Task<bool>> probeAsync,
        Func<int> intervalSecondsProvider,
        Func<bool> canProbe,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _probeAsync = probeAsync;
        _intervalSecondsProvider = intervalSecondsProvider;
        _canProbe = canProbe;
        _delay = delay ?? DefaultDelay;
    }

    /// <summary>探测结束（含恢复流程的复测）；UI 反馈行与计数展示用。</summary>
    public event Action<ProxyGuardProbeState>? ProbeCompleted;

    /// <summary>守护日志（level: info/warning/error；源：appendLog 系列）。</summary>
    public event Action<string, string>? Logged;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public bool IsProbing { get; private set; }

    /// <summary>失败计数快照（设置页反馈行）。</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>
    /// 启动守护循环（幂等；源：startProxyGuardIfNeeded）。<paramref name="probeImmediately"/>
    /// 为 CLI 触发的首启语义：先立即探一次再进入周期循环。
    /// </summary>
    public void Start(bool probeImmediately = false)
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            _consecutiveFailures = 0;
            _loopCancellation = new CancellationTokenSource();
            CancellationToken token = _loopCancellation.Token;
            _loopTask = Task.Run(() => GuardLoopAsync(probeImmediately, token), token);
        }

        Logged?.Invoke("info", CoreL10n.T("log.guard.started"));
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_loopCancellation is null)
            {
                return;
            }

            _loopCancellation.Cancel();
            _loopCancellation.Dispose();
            _loopCancellation = null;
            _loopTask = null;
            _consecutiveFailures = 0;
        }
    }

    /// <summary>外部触发的立即探测（「立即检查」按钮与 CLI 命名事件；源：分布式通知分支）。</summary>
    public async Task<ProxyGuardProbeState> ProbeNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, ConsecutiveFailures);
        }

        return await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task GuardLoopAsync(bool probeImmediately, CancellationToken token)
    {
        if (probeImmediately)
        {
            await ProbeCoreAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }
        }

        while (!token.IsCancellationRequested)
        {
            // 周期下限 30s，每轮重读（源：max(30, intervalSeconds) 的动态语义）
            int interval = Math.Max(MinimumIntervalSeconds, _intervalSecondsProvider());
            try
            {
                await _delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ProbeCoreAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                // 兜底：探测内部的恢复流程异常已吞，此处防御性保证循环永不 Faulted
                Logged?.Invoke("error", CoreL10n.T("log.guard.loop_error", error.Message));
            }
        }
    }

    /// <summary>
    /// 一次探测并推进失败计数（源：proxyGuardProbe——含恢复触发）。
    /// 经信号量串行（立即检查与循环探测不并发，对齐 Swift 的 MainActor）；
    /// 全流程吞除非取消异常——恢复流程恰运行在网络故障期，内核 API 超时不能杀死守护循环
    /// （源：Swift 恢复路径的 catch 容错语义）。
    /// </summary>
    private async Task<ProxyGuardProbeState> ProbeCoreAsync(CancellationToken token)
    {
        if (!_canProbe())
        {
            return new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, ConsecutiveFailures);
        }

        await _probeGate.WaitAsync(token).ConfigureAwait(false);
        IsProbing = true;
        try
        {
            bool reachable = await _probeAsync(token).ConfigureAwait(false);
            int failures;
            string? logLevel = null;
            string? logMessage = null;
            lock (_gate)
            {
                if (reachable)
                {
                    if (_consecutiveFailures > 0)
                    {
                        logLevel = "info";
                        logMessage = CoreL10n.T("log.guard.recovered");
                    }

                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                    logLevel = "warning";
                    logMessage = CoreL10n.T("log.guard.probe_failed", _consecutiveFailures, FailureThreshold);
                }

                failures = _consecutiveFailures;
            }

            // 日志回调在锁外触发（订阅方可能_dispatcher 编组，锁内触发外部回调是反模式）
            if (logMessage is not null)
            {
                Logged?.Invoke(logLevel!, logMessage);
            }

            var state = new ProxyGuardProbeState(
                reachable ? ProxyGuardProbeOutcome.Reachable : ProxyGuardProbeOutcome.Failed,
                reachable ? 0 : failures);
            ProbeCompleted?.Invoke(state);
            if (reachable)
            {
                return state;
            }

            if (failures >= FailureThreshold)
            {
                lock (_gate)
                {
                    _consecutiveFailures = 0;   // 恢复流程清零（源：98-101 行）
                }

                await RecoverAsync(token).ConfigureAwait(false);
            }

            return state;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 恢复期间内核 API 超时/错误不杀死循环：记录后由下一周期重试
            Logged?.Invoke("error", CoreL10n.T("log.guard.probe_error", error.Message));
            return new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, ConsecutiveFailures);
        }
        finally
        {
            IsProbing = false;
            _probeGate.Release();
        }
    }

    /// <summary>恢复：组内切换 → 复测 → 订阅自愈 → 3 秒重测再切换（源：proxyGuardRecover）。</summary>
    private async Task RecoverAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_recoveryInFlight)
            {
                return;
            }

            _recoveryInFlight = true;
        }

        try
        {
            Logged?.Invoke("warning", CoreL10n.T("log.guard.recovery_started"));

            // 1) 组内切换：当前节点坏、组内有活节点就直接切
            if (await RecoverBySwitchingWithinGroupsAsync(token).ConfigureAwait(false)
                && await _probeAsync(token).ConfigureAwait(false))
            {
                Logged?.Invoke("info", CoreL10n.T("log.guard.recovered"));
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 2) 订阅自愈：更新全部提供者，等内核刷新后重测再切换
            await UpdateAllProxyProvidersAsync(token).ConfigureAwait(false);
            try
            {
                await _delay(ProviderRefreshWait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await RecoverBySwitchingWithinGroupsAsync(token).ConfigureAwait(false)
                && await _probeAsync(token).ConfigureAwait(false))
            {
                Logged?.Invoke("info", CoreL10n.T("log.guard.recovered"));
            }
            else
            {
                Logged?.Invoke("error", CoreL10n.T("log.guard.recovery_failed"));
            }
        }
        finally
        {
            lock (_gate)
            {
                _recoveryInFlight = false;
            }
        }
    }

    /// <summary>
    /// 对每个 Selector 组测速：当前节点可用则跳过，否则切到组内最快可用节点（源：recoverBySwitchingWithinGroups）。
    /// 返回是否发生过切换。
    /// </summary>
    private async Task<bool> RecoverBySwitchingWithinGroupsAsync(CancellationToken token)
    {
        bool didSwitch = false;

        ProxyGroupsResponse response = await _client
            .RequestAsync<ProxyGroupsResponse>(MihomoEndpoint.Proxies(), token)
            .ConfigureAwait(false);

        // 仅按 type 过滤（源：Swift 的 type.contains("selector")——不预筛成员，空组交给测速容错）
        IEnumerable<ProxyGroup> selectorGroups = response.Proxies.Values
            .Where(proxy => (proxy.Type ?? string.Empty).ToLowerInvariant().Contains("selector"));

        foreach (ProxyGroup group in selectorGroups)
        {
            GroupDelayMeasurement? results = await MeasureGroupDelaysAsync(group, token).ConfigureAwait(false);
            if (results is null)
            {
                continue;
            }

            string? current = group.Now;
            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            // 当前节点测速有效（含正延迟）则不动（源：results[current].map { $0 > 0 } == true）
            if (results.Values.TryGetValue(current, out int currentDelay) && currentDelay > 0)
            {
                continue;
            }

            // 组内最快可用节点（源：filter { $0.value > 0 && $0.key != current }.min(by: delay)）
            KeyValuePair<string, int>? best = results.Values
                .Where(pair => pair.Value > 0 && pair.Key != current)
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Cast<KeyValuePair<string, int>?>()
                .FirstOrDefault();
            if (best is null)
            {
                continue;
            }

            await _client.RequestNoResponseAsync(
                MihomoEndpoint.SwitchProxy(group.Name, best.Value.Key), token).ConfigureAwait(false);
            Logged?.Invoke("info", CoreL10n.T("log.guard.switched_node", group.Name, best.Value.Key, best.Value.Value));
            didSwitch = true;
        }

        return didSwitch;
    }

    /// <summary>组测速（源：measureGroupDelays——直接请求内核，不经 UI 延迟状态）。</summary>
    private async Task<GroupDelayMeasurement?> MeasureGroupDelaysAsync(ProxyGroup group, CancellationToken token)
    {
        string testUrl = group.TestUrl ?? DefaultProbeUrl;
        int timeout = group.Timeout is int value and > 0 ? value : DefaultHealthcheckTimeoutMilliseconds;
        try
        {
            return await _client.RequestAsync<GroupDelayMeasurement>(
                MihomoEndpoint.GroupDelay(group.Name, testUrl, timeout), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;   // 组测速失败跳过该组（源：catch { return nil }）
        }
    }

    /// <summary>更新全部代理提供者（订阅自愈；源：updateAllProxyProvidersForRecovery）。</summary>
    private async Task UpdateAllProxyProvidersAsync(CancellationToken token)
    {
        ProviderSummary? providers;
        try
        {
            providers = await _client.RequestAsync<ProviderSummary>(
                MihomoEndpoint.ProxyProviders(), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logged?.Invoke("warning", CoreL10n.T("log.guard.providers_fetch_failed", error.Message));
            return;
        }

        List<string> names = providers.Providers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return;
        }

        Logged?.Invoke("info", CoreL10n.T("log.guard.self_heal_all", names.Count));
        foreach (string name in names)
        {
            try
            {
                await _client.RequestNoResponseAsync(
                    MihomoEndpoint.UpdateProxyProvider(name), token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Logged?.Invoke("warning", CoreL10n.T("log.guard.self_heal_failed", name, error.Message));
            }
        }
    }

    private static Task DefaultDelay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);

    public void Dispose()
    {
        Stop();
        _probeGate.Dispose();
    }
}
