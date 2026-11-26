using System.Text;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Streams;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Streams;

/// <summary>
/// StreamCoordinator 行为规格测试（源：StreamCoordinator.swift 的 receiveLoop / start / cancel）。
/// 用可控 delay 与假连接做确定性验证，不等待真实退避时间。
/// </summary>
public class StreamCoordinatorTests : IDisposable
{
    private readonly ManualDelayController _delayController = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly List<string> _payloads = new();
    private readonly List<(string Key, string Message)> _disconnects = new();
    private readonly List<(string Key, Exception Error)> _startErrors = new();
    private readonly List<FakeWebSocketConnection> _connections = new();
    private volatile bool _shouldReconnect = true;

    private StreamCoordinator CreateCoordinator() => new(
        jitter: () => 1.0,
        delay: _delayController.AsFunc(),
        timeProvider: _timeProvider)
    {
        ShouldReconnect = () => _shouldReconnect,
        OnDisconnect = (key, message) => _disconnects.Add((key, message)),
        OnStartError = (key, error) => _startErrors.Add((key, error)),
    };

    private Func<IWebSocketConnection> ConnectionFactory(params FakeWebSocketConnection[] prepared)
    {
        var queue = new Queue<FakeWebSocketConnection>(prepared);
        _connections.AddRange(prepared);
        return () =>
        {
            if (queue.Count == 0)
            {
                throw new InvalidOperationException("测试未预置足够连接");
            }

            return queue.Dequeue();
        };
    }

    private Task OnPayloadAsync(ReadOnlyMemory<byte> payload)
    {
        _payloads.Add(Encoding.UTF8.GetString(payload.Span));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // 每个测试的 coordinator 在 using 中释放；这里无需清理
    }

    [Fact]
    public async Task 有效消息被广播且流处于活动状态()
    {
        var fake = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake), OnPayloadAsync);

        fake.EnqueueMessage(WebSocketStreamMessage.FromText("{\"up\":100,\"down\":200}"));

        await TestAsyncHelper.WaitUntilAsync(() => _payloads.Count == 1, "收到首条 payload");
        Assert.Equal("{\"up\":100,\"down\":200}", _payloads[0]);
        Assert.True(coordinator.IsActive("traffic"));
        Assert.Equal(1, fake.ConnectCount);
    }

    [Fact]
    public async Task 噪声消息不广播但流继续接收()
    {
        var fake = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("memory", ConnectionFactory(fake), OnPayloadAsync);

        fake.EnqueueMessage(WebSocketStreamMessage.FromText("null"));
        fake.EnqueueMessage(WebSocketStreamMessage.FromText("{}"));
        fake.EnqueueMessage(WebSocketStreamMessage.FromText("{\"inuse\":42}"));

        await TestAsyncHelper.WaitUntilAsync(() => _payloads.Count == 1, "跳过噪声后收到有效 payload");
        Assert.Equal("{\"inuse\":42}", _payloads[0]);
        Assert.Empty(_delayController.RequestedDelays);
    }

    [Fact]
    public async Task 断开后按退避序列延迟并重连()
    {
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        var fake3 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2, fake3), OnPayloadAsync);

        fake1.EnqueueMessage(WebSocketStreamMessage.FromText("{\"up\":1}"));
        await TestAsyncHelper.WaitUntilAsync(() => _payloads.Count == 1, "首条 payload");
        fake1.EnqueueError(new Exception("reset by peer"));

        await _delayController.WaitForDelayCountAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(1), _delayController.RequestedDelays[0]);
        _delayController.CompleteCurrentDelay();   // 时间流逝 → 重连

        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "第二条连接建立");
        // 第二次断开用不同错误消息，避免被 2 秒同消息节流（规格行为）
        fake2.EnqueueError(new Exception("reset by peer 2"));

        await _delayController.WaitForDelayCountAsync(2);
        Assert.Equal(TimeSpan.FromSeconds(2), _delayController.RequestedDelays[1]);
        _delayController.CompleteCurrentDelay();

        await TestAsyncHelper.WaitUntilAsync(() => fake3.ConnectCount == 1, "第三条连接建立");
        Assert.Equal(2, _disconnects.Count);
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 收到有效载荷后重连延迟从头开始()
    {
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        var fake3 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2, fake3), OnPayloadAsync);

        fake1.EnqueueError(new Exception("reset by peer"));     // 未收到任何有效消息
        await _delayController.WaitForDelayCountAsync(1);
        _delayController.CompleteCurrentDelay();

        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "第二条连接建立");
        fake2.EnqueueMessage(WebSocketStreamMessage.FromText("{\"up\":1}"));   // 有效载荷 → 计数归零
        await TestAsyncHelper.WaitUntilAsync(() => _payloads.Count == 1, "有效 payload");
        fake2.EnqueueError(new Exception("reset by peer"));

        await _delayController.WaitForDelayCountAsync(2);
        // 第一轮 attempt=0 → 1s；有效载荷重置后 attempt 又从 0 开始 → 1s
        Assert.Equal(TimeSpan.FromSeconds(1), _delayController.RequestedDelays[1]);
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 噪声消息不重置失败计数()
    {
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        var fake3 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2, fake3), OnPayloadAsync);

        fake1.EnqueueError(new Exception("reset by peer"));
        await _delayController.WaitForDelayCountAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(1), _delayController.RequestedDelays[0]);
        _delayController.CompleteCurrentDelay();

        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "第二条连接建立");
        fake2.EnqueueMessage(WebSocketStreamMessage.FromText("null"));   // 噪声：不重置计数
        fake2.EnqueueError(new Exception("reset by peer"));

        await _delayController.WaitForDelayCountAsync(2);
        // 噪声不重置 → attempt 延续为 1 → 2s
        Assert.Equal(TimeSpan.FromSeconds(2), _delayController.RequestedDelays[1]);
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 不应重连时以最大延迟轮询等待()
    {
        _shouldReconnect = false;
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2), OnPayloadAsync);

        fake1.EnqueueError(new Exception("reset by peer"));

        await _delayController.WaitForDelayCountAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(8), _delayController.RequestedDelays[0]);
        Assert.Equal(1, fake1.ConnectCount);   // 未发生重连
        _delayController.CompleteCurrentDelay();   // 放行第一个 8s 轮询

        await _delayController.WaitForDelayCountAsync(2);
        Assert.Equal(TimeSpan.FromSeconds(8), _delayController.RequestedDelays[1]);
        Assert.Equal(1, fake1.ConnectCount);

        _shouldReconnect = true;              // 恢复重连条件
        _delayController.CompleteCurrentDelay();
        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "条件恢复后重连");
        coordinator.Cancel("traffic");
    }

    [Fact]
    public void 连接工厂抛错时上报启动错误且不重试()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start(
            "traffic",
            () => throw MihomoApiException.InvalidUrl(),
            OnPayloadAsync);

        Assert.Single(_startErrors);
        Assert.Equal("traffic", _startErrors[0].Key);
        Assert.Empty(_delayController.RequestedDelays);
        Assert.False(coordinator.IsActive("traffic"));
    }

    [Fact]
    public async Task 连接失败被视为断线走重连()
    {
        var fake1 = new FakeWebSocketConnection { ConnectError = new Exception("connect refused") };
        var fake2 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2), OnPayloadAsync);

        await _delayController.WaitForDelayCountAsync(1);
        Assert.Single(_disconnects);
        _delayController.CompleteCurrentDelay();

        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "重连成功");
        Assert.True(coordinator.IsActive("traffic"));
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 载荷处理器抛错被视为断线并重连()
    {
        // Swift 原版此处会留下幽灵会话（接收循环死掉但 active 状态残留）；
        // Windows 版有意修正为按断线重连，见迁移报告
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start(
            "traffic",
            ConnectionFactory(fake1, fake2),
            payload =>
            {
                _payloads.Add(Encoding.UTF8.GetString(payload.Span));
                throw new Exception("handler failed");
            });

        fake1.EnqueueMessage(WebSocketStreamMessage.FromText("{\"up\":1}"));
        await TestAsyncHelper.WaitUntilAsync(() => _disconnects.Count == 1, "处理器错误上报为断线");
        Assert.Contains("handler failed", _disconnects[0].Message);

        await _delayController.WaitForDelayCountAsync(1);
        _delayController.CompleteCurrentDelay();
        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "重连");
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 断开日志按2秒同消息节流()
    {
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2), OnPayloadAsync);

        fake1.EnqueueError(new Exception("reset by peer"));
        await TestAsyncHelper.WaitUntilAsync(() => _disconnects.Count == 1, "首次断开上报");
        _delayController.CompleteCurrentDelay();
        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "重连");

        _timeProvider.Advance(TimeSpan.FromMilliseconds(1999));   // 2 秒内：同消息不再上报
        fake2.EnqueueError(new Exception("reset by peer"));
        await TestAsyncHelper.WaitUntilAsync(() => _delayController.RequestedDelays.Count >= 2, "第二次退避");
        Assert.Single(_disconnects);

        _delayController.CompleteCurrentDelay();
        var fake3 = new FakeWebSocketConnection();
        // 需要第三条连接继续验证：重新启动流
        coordinator.Start("traffic", ConnectionFactory(fake3), OnPayloadAsync);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(100));    // 累计超过 2 秒
        fake3.EnqueueError(new Exception("reset by peer"));
        await TestAsyncHelper.WaitUntilAsync(() => _disconnects.Count == 2, "节流窗口外再次上报");
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 不同错误消息不受节流抑制()
    {
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        var fake3 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1, fake2, fake3), OnPayloadAsync);

        fake1.EnqueueError(new Exception("error A"));
        await TestAsyncHelper.WaitUntilAsync(() => _disconnects.Count == 1, "错误 A 上报");
        await _delayController.WaitForDelayCountAsync(1);
        _delayController.CompleteCurrentDelay();
        await TestAsyncHelper.WaitUntilAsync(() => fake2.ConnectCount == 1, "重连");

        fake2.EnqueueError(new Exception("error A"));      // 相同消息、2 秒内：抑制
        await _delayController.WaitForDelayCountAsync(2);
        Assert.Single(_disconnects);
        _delayController.CompleteCurrentDelay();
        await TestAsyncHelper.WaitUntilAsync(() => fake3.ConnectCount == 1, "再次重连");

        fake3.EnqueueError(new Exception("error B"));      // 不同消息：立即上报
        await TestAsyncHelper.WaitUntilAsync(() => _disconnects.Count == 2, "错误 B 上报");
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task Cancel停止流并清空活动状态()
    {
        var fake = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake), OnPayloadAsync);
        fake.EnqueueMessage(WebSocketStreamMessage.FromText("{\"up\":1}"));
        await TestAsyncHelper.WaitUntilAsync(() => _payloads.Count == 1, "payload");
        Assert.True(coordinator.IsActive("traffic"));

        coordinator.Cancel("traffic");
        await TestAsyncHelper.WaitUntilAsync(() => !coordinator.IsActive("traffic"), "取消后失活");
    }

    [Fact]
    public async Task Cancel默认清空重连状态()
    {
        var fake1 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1), OnPayloadAsync);
        fake1.EnqueueError(new Exception("reset by peer"));
        await _delayController.WaitForDelayCountAsync(1);
        // 触发过一次退避（attempt=1），但未放行时间
        Assert.Equal(TimeSpan.FromSeconds(1), _delayController.RequestedDelays[0]);

        coordinator.Cancel("traffic");

        var fake2 = new FakeWebSocketConnection();
        coordinator.Start("traffic", ConnectionFactory(fake2), OnPayloadAsync);
        fake2.EnqueueError(new Exception("reset by peer"));
        await _delayController.WaitForDelayCountAsync(2);
        // Cancel 已清空计数 → 新一轮从 1s 开始
        Assert.Equal(TimeSpan.FromSeconds(1), _delayController.RequestedDelays[1]);
        coordinator.Cancel("traffic");
    }

    [Fact]
    public async Task 重复Start同键时旧连接被释放且保留计数()
    {
        var fake1 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1), OnPayloadAsync);
        await TestAsyncHelper.WaitUntilAsync(() => fake1.ConnectCount == 1, "第一条连接");

        var fake2 = new FakeWebSocketConnection();
        coordinator.Start("traffic", ConnectionFactory(fake2), OnPayloadAsync);
        await TestAsyncHelper.WaitUntilAsync(() => fake1.DisposeCount == 1, "旧连接释放");
        Assert.True(coordinator.IsActive("traffic"));

        fake2.EnqueueError(new Exception("reset by peer"));
        await _delayController.WaitForDelayCountAsync(1);
        // 未收到有效载荷、未 Cancel：计数保留 → 第一次退避即 1s（attempt 0）
        Assert.Equal(TimeSpan.FromSeconds(1), _delayController.RequestedDelays[0]);
        coordinator.Cancel("traffic");
    }

    [Fact]
    public void CancelAll停止全部流()
    {
        var fake1 = new FakeWebSocketConnection();
        var fake2 = new FakeWebSocketConnection();
        using var coordinator = CreateCoordinator();
        coordinator.Start("traffic", ConnectionFactory(fake1), OnPayloadAsync);
        coordinator.Start("memory", ConnectionFactory(fake2), OnPayloadAsync);
        Assert.True(coordinator.IsActive("traffic"));
        Assert.True(coordinator.IsActive("memory"));

        coordinator.CancelAll();

        Assert.False(coordinator.IsActive("traffic"));
        Assert.False(coordinator.IsActive("memory"));
    }
}
