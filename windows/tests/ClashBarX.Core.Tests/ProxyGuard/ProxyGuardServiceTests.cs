using System.Collections.Concurrent;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.ProxyGuard;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.ProxyGuard;

/// <summary>自动代理守护规格测试（源：AppViewModel+ProxyGuard.swift 逐行对照）。</summary>
public class ProxyGuardServiceTests
{
    private static readonly TimeSpan ProviderWait = ProxyGuardService.ProviderRefreshWait;

    private sealed class FakeApiClient : IMihomoApiClient
    {
        public List<MihomoEndpointKind> FailKinds { get; } = new();
        public ConcurrentQueue<MihomoEndpoint> Requests { get; } = new();
        public Dictionary<MihomoEndpointKind, Func<object>> Responses { get; } = new();

        public int Count(MihomoEndpointKind kind) => Requests.Count(endpoint => endpoint.Kind == kind);

        public void UpdateCredentials(string controller, string? secret) { }

        public Task<T> RequestAsync<T>(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
            where T : class
        {
            Requests.Enqueue(endpoint);
            if (FailKinds.Contains(endpoint.Kind))
            {
                throw new HttpRequestException($"脚本化失败：{endpoint.Kind}");
            }

            return Task.FromResult((T)Responses[endpoint.Kind]());
        }

        public Task RequestNoResponseAsync(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(endpoint);
            if (FailKinds.Contains(endpoint.Kind))
            {
                throw new HttpRequestException($"脚本化失败：{endpoint.Kind}");
            }

            return Task.CompletedTask;
        }

        public IWebSocketConnection CreateWebSocketConnection(MihomoEndpoint endpoint) =>
            throw new NotSupportedException("守护测试不使用 WebSocket");

        public void Dispose() { }
    }

    private sealed class Harness
    {
        public FakeApiClient Client = new();
        public ConcurrentQueue<string> Sequence = new();   // 全序：probe 与 delay 交错记录
        public ConcurrentQueue<(string Level, string Message)> Logs = new();
        public ConcurrentQueue<ProxyGuardProbeState> ProbeStates = new();
        public ConcurrentQueue<TimeSpan> RequestedDelays { get; } = new();
        public List<bool> ProbeScript = new();
        public int ProbeCalls;
        public int IntervalSeconds = 300;
        public bool CanProbe = true;

        public Func<CancellationToken, Task<bool>> ProbeAsync =>
            async cancellationToken =>
            {
                // probe 由守护循环串行 await，无需原子计数
                int index = ProbeCalls + 1;
                if (index > ProbeScript.Count)
                {
                    // 脚本耗尽：挂起直到取消（冻结计数与序列，配合零延迟驱动防 CPU 尖峰）
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return true;
                }

                ProbeCalls = index;
                Sequence.Enqueue("probe");
                return ProbeScript[index - 1];
            };

        /// <summary>零延迟驱动：记录请求时长后立即完成（循环全速，恢复 3 秒等待同理）。</summary>
        public Func<TimeSpan, CancellationToken, Task> DelayAsync => (delay, cancellationToken) =>
        {
            RequestedDelays.Enqueue(delay);
            Sequence.Enqueue($"delay:{delay.TotalSeconds:F0}s");
            return Task.CompletedTask;
        };

        public ProxyGuardService Service = null!;

        public Harness WithDefaults()
        {
            Client.Responses[MihomoEndpointKind.Proxies] = () => new ProxyGroupsResponse(
                new Dictionary<string, ProxyGroup>());
            Client.Responses[MihomoEndpointKind.ProxyProviders] = () => new ProviderSummary(
                new Dictionary<string, ProviderDetail>());
            Service = new ProxyGuardService(
                Client,
                ProbeAsync,
                intervalSecondsProvider: () => IntervalSeconds,
                canProbe: () => CanProbe,
                delay: DelayAsync);
            Service.Logged += (level, message) => Logs.Enqueue((level, message));
            Service.ProbeCompleted += state => ProbeStates.Enqueue(state);
            return this;
        }

        public Task WaitForLogAsync(Func<string, bool> matcher, string description) =>
            TestAsyncHelper.WaitUntilAsync(() => Logs.Any(entry => matcher(entry.Message)), description);

        public Task WaitUntilAsync(Func<bool> condition, string description) =>
            TestAsyncHelper.WaitUntilAsync(condition, description);

        public IReadOnlyList<string> SequenceSnapshot() => Sequence.ToArray();

        public IReadOnlyList<TimeSpan> DelaySnapshot() => RequestedDelays.ToArray();
    }

    private static ProxyGroup SelectorGroup(string now, IReadOnlyList<string> members) => new(
        "分组", "Selector", now, members, null, null, null, null, Array.Empty<int>());

    private static GroupDelayMeasurement Delays(params (string Node, int Delay)[] values) =>
        new(values.ToDictionary(value => value.Node, value => value.Delay));

    [Fact]
    public async Task 周期下限30秒且每轮动态读取()
    {
        Harness harness = new Harness().WithDefaults();
        harness.IntervalSeconds = 10;   // 低于下限
        harness.ProbeScript = [true, true];

        harness.Service.Start();
        await harness.WaitUntilAsync(() => harness.ProbeCalls >= 2, "两轮探测");

        Assert.All(harness.DelaySnapshot(), delay => Assert.Equal(30, delay.TotalSeconds));
        harness.Service.Dispose();
    }

    [Fact]
    public async Task CLI首启先探一次再进周期循环()
    {
        Harness harness = new Harness().WithDefaults();
        harness.ProbeScript = [true, true, true];

        harness.Service.Start(probeImmediately: true);
        await harness.WaitUntilAsync(() => harness.Sequence.Count >= 2, "首探与首个周期延迟");

        // 先探再睡：序列首项必须是 probe（源：CLI 触发的首启语义）
        Assert.Equal("probe", harness.SequenceSnapshot()[0]);
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 未启动时立即探测返回失败()
    {
        Harness harness = new Harness().WithDefaults();

        ProxyGuardProbeState state = await harness.Service.ProbeNowAsync();

        Assert.Equal(ProxyGuardProbeOutcome.Failed, state.Outcome);
        Assert.Equal(0, harness.ProbeCalls);
    }

    [Fact]
    public async Task 探测不可运行时不推进计数()
    {
        Harness harness = new Harness().WithDefaults();
        harness.CanProbe = false;

        harness.Service.Start();
        await harness.WaitUntilAsync(() => harness.Sequence.Count >= 1, "首轮延迟入序");
        await Task.Delay(100);

        Assert.Equal(0, harness.ProbeCalls);   // canProbe 拦截：探测函数从未被调用
        Assert.Equal(0, harness.Service.ConsecutiveFailures);
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 停止后可重启()
    {
        Harness harness = new Harness().WithDefaults();

        harness.Service.Start();
        Assert.True(harness.Service.IsRunning);
        harness.Service.Stop();
        Assert.False(harness.Service.IsRunning);

        harness.Service.Start();
        Assert.True(harness.Service.IsRunning);
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 连续三次失败触发组内切换恢复()
    {
        Harness harness = new Harness().WithDefaults();
        // 当前节点测速 0（不可用），组内最快可用节点 120ms
        harness.Client.Responses[MihomoEndpointKind.Proxies] = () => new ProxyGroupsResponse(
            new Dictionary<string, ProxyGroup>
            {
                ["分组"] = SelectorGroup("坏节点", new[] { "坏节点", "好节点", "中节点" }),
            });
        harness.Client.Responses[MihomoEndpointKind.GroupDelay] = () => Delays(
            ("坏节点", 0), ("好节点", 120), ("中节点", 200));
        harness.ProbeScript = [false, false, false, true];

        harness.Service.Start();
        await harness.WaitForLogAsync(message => message.Contains("网络连通已恢复"), "恢复日志");

        // 只切到组内最快可用（120ms 的好节点），并经 SwitchProxy 下发
        Assert.Equal(1, harness.Client.Count(MihomoEndpointKind.SwitchProxy));
        Assert.Equal(0, harness.Service.ConsecutiveFailures);   // 恢复流程清零
        Assert.Equal(4, harness.ProbeCalls);   // 3 次失败 + 1 次复测
        Assert.Equal(0, harness.Client.Count(MihomoEndpointKind.UpdateProxyProvider));   // 未触发自愈
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 当前节点可用时不切换()
    {
        Harness harness = new Harness().WithDefaults();
        harness.Client.Responses[MihomoEndpointKind.Proxies] = () => new ProxyGroupsResponse(
            new Dictionary<string, ProxyGroup>
            {
                ["分组"] = SelectorGroup("好节点", new[] { "坏节点", "好节点" }),
            });
        harness.Client.Responses[MihomoEndpointKind.GroupDelay] = () => Delays(
            ("坏节点", 0), ("好节点", 80));
        harness.ProbeScript = [false, false, false];

        harness.Service.Start();
        // 无切换发生（didSwitch=false）→ 走自愈分支直到恢复流程终态（源：&& 短路语义）
        await harness.WaitForLogAsync(message => message.Contains("恢复失败"), "恢复流程终态");

        // 当前节点测速有效（80ms）→ 从不切换
        Assert.Equal(0, harness.Client.Count(MihomoEndpointKind.SwitchProxy));
        Assert.True(harness.Client.Count(MihomoEndpointKind.GroupDelay) >= 1);   // 测速过当前节点
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 组内切换无效时订阅自愈并3秒后重测再切换()
    {
        Harness harness = new Harness().WithDefaults();
        // 第一轮组切换有可用节点但复测失败（probe 第 4 次 false）；
        // provider 更新后第二轮组测速换可用节点，复测成功
        var groupDelays = new Queue<GroupDelayMeasurement>();
        groupDelays.Enqueue(Delays(("坏节点", 0), ("好节点", 100)));
        groupDelays.Enqueue(Delays(("坏节点", 0), ("新节点", 150)));
        harness.Client.Responses[MihomoEndpointKind.Proxies] = () => new ProxyGroupsResponse(
            new Dictionary<string, ProxyGroup>
            {
                ["分组"] = SelectorGroup("坏节点", new[] { "坏节点", "好节点", "新节点" }),
            });
        harness.Client.Responses[MihomoEndpointKind.GroupDelay] = () => groupDelays.Dequeue();
        harness.Client.Responses[MihomoEndpointKind.ProxyProviders] = () => new ProviderSummary(
            new Dictionary<string, ProviderDetail>
            {
                ["订阅A"] = new ProviderDetail("订阅A", "HTTP", null, null, null, null, null, null),
                ["订阅B"] = new ProviderDetail("订阅B", "HTTP", null, null, null, null, null, null),
            });
        harness.ProbeScript = [false, false, false, false, true];

        harness.Service.Start();
        await harness.WaitForLogAsync(message => message.Contains("网络连通已恢复"), "自愈恢复");

        Assert.Equal(2, harness.Client.Count(MihomoEndpointKind.UpdateProxyProvider));   // 两个 provider 都更新
        Assert.Contains(ProviderWait, harness.DelaySnapshot());   // 3 秒等待在列
        Assert.Equal(2, harness.Client.Count(MihomoEndpointKind.SwitchProxy));   // 两轮切换
        Assert.Equal(5, harness.ProbeCalls);   // 3 失败 + 首轮复测 + 自愈后复测
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 自愈后仍无可用节点时记录恢复失败()
    {
        Harness harness = new Harness().WithDefaults();
        harness.Client.Responses[MihomoEndpointKind.Proxies] = () => new ProxyGroupsResponse(
            new Dictionary<string, ProxyGroup>
            {
                ["分组"] = SelectorGroup("坏节点", new[] { "坏节点" }),
            });
        harness.Client.Responses[MihomoEndpointKind.GroupDelay] = () => Delays(("坏节点", 0));   // 全组挂
        harness.Client.Responses[MihomoEndpointKind.ProxyProviders] = () => new ProviderSummary(
            new Dictionary<string, ProviderDetail>
            {
                ["订阅A"] = new ProviderDetail("订阅A", "HTTP", null, null, null, null, null, null),
            });
        harness.ProbeScript = [false, false, false];   // 无切换发生，复测全部短路

        harness.Service.Start();
        await harness.WaitForLogAsync(message => message.Contains("恢复失败"), "恢复失败日志");

        Assert.Equal(0, harness.Client.Count(MihomoEndpointKind.SwitchProxy));   // 组内无可用，从不切换
        Assert.Equal(1, harness.Client.Count(MihomoEndpointKind.UpdateProxyProvider));   // 首轮组切无效 → 直接自愈
        Assert.Contains(ProviderWait, harness.DelaySnapshot());
        harness.Service.Dispose();
    }

    [Fact]
    public async Task 非selector组不参与切换()
    {
        Harness harness = new Harness().WithDefaults();
        harness.Client.Responses[MihomoEndpointKind.Proxies] = () => new ProxyGroupsResponse(
            new Dictionary<string, ProxyGroup>
            {
                ["自动组"] = new ProxyGroup("自动组", "URLTest", "坏节点",
                    new[] { "坏节点", "好节点" }, null, null, null, null, Array.Empty<int>()),
            });
        harness.ProbeScript = [false, false, false];

        harness.Service.Start();
        await harness.WaitForLogAsync(message => message.Contains("恢复失败"), "恢复流程终态");

        Assert.Equal(0, harness.Client.Count(MihomoEndpointKind.GroupDelay));   // 非 selector 不测速
        Assert.Equal(0, harness.Client.Count(MihomoEndpointKind.SwitchProxy));
        harness.Service.Dispose();
    }
}
