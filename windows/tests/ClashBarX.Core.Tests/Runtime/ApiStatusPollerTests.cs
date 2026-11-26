using System.Net;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Runtime;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Runtime;

/// <summary>
/// API 就绪轮询规格测试（源：AppViewModel+Polling.swift 的 runRefresh 状态机）：
/// 成功 → Healthy、失败 → Degraded、错误指纹去重、状态迁移不重复触发、
/// 间隔每轮动态获取、启动幂等、销毁停止循环。
/// </summary>
public class ApiStatusPollerTests
{
    private sealed class PollerHarness
    {
        private readonly List<TimeSpan> _requestedDelays = new();
        private readonly List<TaskCompletionSource<bool>> _pendingDelays = new();
        private readonly object _gate = new();

        /// <summary>诊断视图：延迟请求总数（同步读需持 DelayGate）。</summary>
        public object DelayGate => _gate;

        public int DelayCount
        {
            get { lock (_gate) return _requestedDelays.Count; }
        }

        public StubHttpMessageHandler Handler { get; }

        public List<ApiHealth> StatusChanges { get; } = new();

        public List<string> Errors { get; } = new();

        public List<string> Versions { get; } = new();

        public PollerHarness(params StubHttpMessageHandler.ScriptStep[] steps)
        {
            Handler = steps.Length > 0
                ? new StubHttpMessageHandler(steps)
                : StubHttpMessageHandler.RespondJson("{\"version\":\"v1.19.34\"}");
        }

        public IReadOnlyList<TimeSpan> RequestedDelays
        {
            get { lock (_gate) return _requestedDelays.ToArray(); }
        }

        public Task WaitForDelayRequestAsync(int count) =>
            TestAsyncHelper.WaitUntilAsync(() => { lock (_gate) return _requestedDelays.Count >= count; },
                $"第 {count} 个延迟请求");

        public Func<TimeSpan, CancellationToken, Task> DelayFunc => (span, ct) =>
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                _requestedDelays.Add(span);
                completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingDelays.Add(completion);
            }

            ct.Register(() => completion.TrySetCanceled(ct));
            return completion.Task;
        };

        /// <summary>放行所有已请求的延迟（每次放行一个，驱动一轮轮询）。</summary>
        public void CompleteOneDelay()
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (_pendingDelays.Count == 0)
                {
                    throw new InvalidOperationException("没有待放行的延迟");
                }

                completion = _pendingDelays[0];
                _pendingDelays.RemoveAt(0);
            }

            completion.TrySetResult(true);
        }
    }

    private static (ApiStatusPoller Poller, PollerHarness Harness) Create(
        Func<TimeSpan>? intervalProvider = null,
        params StubHttpMessageHandler.ScriptStep[] steps)
    {
        var harness = new PollerHarness(steps);
        var client = new MihomoApiClient("127.0.0.1:9090", null, harness.Handler);
        var poller = new ApiStatusPoller(client, intervalProvider, harness.DelayFunc);
        return (poller, harness);
    }

    [Fact]
    public async Task 探测成功进入健康状态并上报版本()
    {
        var (poller, harness) = Create();
        poller.StatusChanged += harness.StatusChanges.Add;
        poller.ErrorLogged += harness.Errors.Add;
        poller.VersionChanged += harness.Versions.Add;

        poller.Start();
        await harness.WaitForDelayRequestAsync(1);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.StatusChanges.Count == 1, "状态迁移");

        Assert.Equal(ApiHealth.Healthy, poller.Status);
        Assert.Equal(ApiHealth.Healthy, harness.StatusChanges.Single());
        Assert.Equal("v1.19.34", poller.Version);
        Assert.Equal(new[] { "v1.19.34" }, harness.Versions);
        Assert.Empty(harness.Errors);
        poller.Dispose();
    }

    [Fact]
    public async Task 探测失败进入降级状态并记录一次错误()
    {
        // GET 的传输层失败会被客户端重试至多 3 次：提供 3 步同错误让最终异常保持为 HttpRequestException
        var (poller, harness) = Create(steps: new[]
        {
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("refused")),
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("refused")),
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("refused")),
        });
        poller.StatusChanged += harness.StatusChanges.Add;
        poller.ErrorLogged += harness.Errors.Add;

        poller.Start();
        await harness.WaitForDelayRequestAsync(1);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.StatusChanges.Count == 1, "降级迁移");

        Assert.Equal(ApiHealth.Degraded, poller.Status);
        Assert.Contains("refused", harness.Errors.Single());
        Assert.Null(poller.Version);
        poller.Dispose();
    }

    [Fact]
    public async Task 同指纹错误不重复记录()
    {
        // 两轮失败，各需 3 步（GET 传输失败重试 3 次）以保持异常类型一致
        var (poller, harness) = Create(steps: Enumerable.Range(0, 6)
            .Select(_ => new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("refused")))
            .ToArray());
        poller.ErrorLogged += harness.Errors.Add;
        poller.StatusChanged += harness.StatusChanges.Add;

        poller.Start();
        // 第一轮：失败 + 记录
        await harness.WaitForDelayRequestAsync(1);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.Errors.Count == 1, "首次错误记录");

        // 第二轮：同错误 → 不再记录（指纹去重）
        await harness.WaitForDelayRequestAsync(2);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.Errors.Count == 2 || harness.Handler.Requests.Count == 6, "第二轮重试完成");
        await Task.Delay(200);
        Assert.Single(harness.Errors);

        poller.Dispose();
    }

    [Fact]
    public async Task 恢复成功清除指纹_再失败会重新记录()
    {
        var handler = new StubHttpMessageHandler(
            new[]
            {
                new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("boom-1")),
                new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("boom-1")),
                new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("boom-1")),
                new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"version\":\"ok\"}")),
                new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("boom-1")),
                new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("boom-1")),
                new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError("boom-1")),
            });
        var harness = new PollerHarness();
        var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var poller = new ApiStatusPoller(client, intervalProvider: null, harness.DelayFunc);
        poller.ErrorLogged += harness.Errors.Add;
        poller.StatusChanged += harness.StatusChanges.Add;

        poller.Start();
        // 轮 1：失败记录一次
        await harness.WaitForDelayRequestAsync(1);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.Errors.Count == 1, "首次错误");

        // 轮 2：成功 → 健康且指纹清空
        await harness.WaitForDelayRequestAsync(2);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => poller.Status == ApiHealth.Healthy, "恢复健康");

        // 轮 3：同错误再次失败 → 因为指纹已清空而重新记录
        await harness.WaitForDelayRequestAsync(3);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.Errors.Count == 2, "清空后重新记录");

        Assert.Equal(2, harness.Errors.Count);
        poller.Dispose();
    }

    [Fact]
    public async Task 状态不重复触发()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"version\":\"1\"}")),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"version\":\"1\"}")));
        var harness = new PollerHarness();
        var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var poller = new ApiStatusPoller(client, intervalProvider: null, harness.DelayFunc);
        poller.StatusChanged += harness.StatusChanges.Add;

        poller.Start();
        for (int round = 1; round <= 2; round++)
        {
            await harness.WaitForDelayRequestAsync(round);
            harness.CompleteOneDelay();
        }

        await TestAsyncHelper.WaitUntilAsync(() => handler.Requests.Count == 2, "两轮请求完成");
        Assert.Single(harness.StatusChanges);   // 连续成功只触发一次 Healthy
        poller.Dispose();
    }

    [Fact]
    public async Task 间隔每轮动态获取()
    {
        int providerCalls = 0;
        var (poller, harness) = Create(intervalProvider: () =>
        {
            providerCalls++;
            return providerCalls == 1 ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(1);
        });

        poller.Start();
        await harness.WaitForDelayRequestAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(4), harness.RequestedDelays[0]);
        harness.CompleteOneDelay();
        await TestAsyncHelper.WaitUntilAsync(() => harness.Handler.Requests.Count == 1, "首轮请求");

        await harness.WaitForDelayRequestAsync(2);
        Assert.Equal(TimeSpan.FromSeconds(1), harness.RequestedDelays[1]);
        poller.Dispose();
    }

    [Fact]
    public async Task 启动幂等_只跑一个循环()
    {
        var (poller, harness) = Create();
        poller.Start();
        poller.Start();
        await harness.WaitForDelayRequestAsync(1);
        await Task.Delay(150);
        Assert.Single(harness.RequestedDelays);   // 没有第二个循环在请求延迟
        poller.Dispose();
    }

    [Fact]
    public async Task 销毁会停止循环()
    {
        var (poller, harness) = Create();
        poller.Start();
        await harness.WaitForDelayRequestAsync(1);

        poller.Dispose();
        await Task.Delay(150);
        int delayCount = harness.RequestedDelays.Count;
        harness.CompleteOneDelay();   // 放行挂起的延迟，循环应感知取消而退出

        await Task.Delay(150);
        Assert.Equal(delayCount, harness.RequestedDelays.Count);   // 不再请求新的延迟
    }

    [Fact]
    public void 非成功状态码也会进入降级()
    {
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Response: StubHttpMessageHandler.Text("secret required", HttpStatusCode.Unauthorized)));
        var harness = new PollerHarness();
        var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        using var poller = new ApiStatusPoller(client, intervalProvider: null, harness.DelayFunc);
        var errors = new List<string>();
        poller.ErrorLogged += errors.Add;

        poller.Start();
        harness.WaitForDelayRequestAsync(1).GetAwaiter().GetResult();
        harness.CompleteOneDelay();
        TestAsyncHelper.WaitUntilAsync(() => poller.Status == ApiHealth.Degraded, "401 进入降级").GetAwaiter().GetResult();

        Assert.Single(errors);
    }
}
