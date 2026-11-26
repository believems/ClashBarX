using ClashBarX.Core.SystemProxy;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.SystemProxy;

/// <summary>
/// 系统代理编排规格测试（源：AppViewModel+SystemProxy.swift 的意图/守护/去抖语义）。
/// 用内存假控制器验证：显式开关、外部改动守护回写、无意图不越权、自写抑制。
/// </summary>
public class SystemProxyServiceTests
{
    private sealed class FakeController : ISystemProxyController
    {
        private readonly object _gate = new();

        public SystemProxySnapshot State { get; private set; } = SystemProxySnapshot.Disabled;

        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public List<SystemProxyTarget> AppliedTargets { get; } = new();

        public SystemProxySnapshot Read()
        {
            lock (_gate) return State;
        }

        public void Enable(SystemProxyTarget target)
        {
            lock (_gate)
            {
                EnableCalls++;
                AppliedTargets.Add(target);
                State = new SystemProxySnapshot(true, target.ServerDisplay, target.BypassList);
            }
        }

        public void Disable()
        {
            lock (_gate)
            {
                DisableCalls++;
                State = State with { Enabled = false };
            }
        }

        /// <summary>模拟外部程序改注册表。</summary>
        public void ExternalWrite(SystemProxySnapshot snapshot)
        {
            lock (_gate) State = snapshot;
        }
    }

    private static SystemProxyTarget Target(int port = 7890) =>
        new("127.0.0.1", port, new[] { "localhost", "127.*", "10.*" });

    [Fact]
    public void 显式启用记录意图并写注册表()
    {
        var controller = new FakeController();
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.Zero);
        var logs = new List<string>();
        service.Logged += logs.Add;

        service.Enable(Target());

        Assert.True(controller.State.Enabled);
        Assert.Equal("127.0.0.1:7890", controller.State.Server);
        Assert.Equal(Target(), service.ExpectedTarget);
        Assert.Contains(logs, line => line.Contains("enabled 127.0.0.1:7890"));
    }

    [Fact]
    public void 显式禁用清除意图()
    {
        var controller = new FakeController();
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.Zero);
        service.Enable(Target());

        service.Disable();

        Assert.False(controller.State.Enabled);
        Assert.Null(service.ExpectedTarget);
        Assert.Equal("127.0.0.1:7890", controller.State.Server);   // Server 值保留
    }

    [Fact]
    public async Task 外部改动偏离意图时守护回写()
    {
        var controller = new FakeController();
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.Zero);
        var logs = new List<string>();
        service.Logged += logs.Add;
        service.Enable(Target());
        // 真实时序：自写注册表先触发一轮 watcher 通知（被抑制）
        service.NotifyRegistryChanged();

        controller.ExternalWrite(new SystemProxySnapshot(false, null, Array.Empty<string>()));
        service.NotifyRegistryChanged();
        // 等终态而非 EnableCalls 计数：计数递增与断言之间在慢机（CI 首跑 flaky 根因）
        // 存在观察窗口，以最终 State 为准消除撕裂。
        await TestAsyncHelper.WaitUntilAsync(
            () => controller.State is { Enabled: true, Server: "127.0.0.1:7890" }, "守护回写发生");

        Assert.True(controller.State.Enabled);
        Assert.Equal("127.0.0.1:7890", controller.State.Server);
        Assert.Contains(logs, line => line.Contains("repaired"));
    }

    [Fact]
    public async Task 无意图时外部改动不越权回写()
    {
        var controller = new FakeController();
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.Zero);
        var enabledChanges = new List<bool>();
        service.EnabledChanged += enabledChanges.Add;

        controller.ExternalWrite(new SystemProxySnapshot(true, "1.2.3.4:8080", new[] { "x" }));
        service.NotifyRegistryChanged();
        await Task.Delay(200);

        Assert.Equal(0, controller.EnableCalls);   // 不回写
        Assert.Contains(true, enabledChanges);      // 但同步了观测状态
        Assert.True(service.LastSnapshot!.Enabled);
    }

    [Fact]
    public async Task 自写引发的变更通知被抑制()
    {
        var controller = new FakeController();
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.Zero);
        service.Enable(Target());
        int enableCallsBefore = controller.EnableCalls;

        // Enable 自身引发的注册表通知（第一轮被抑制）
        service.NotifyRegistryChanged();
        await Task.Delay(150);
        Assert.Equal(enableCallsBefore, controller.EnableCalls);
    }

    [Fact]
    public async Task 周期核对发现偏离时修复()
    {
        var controller = new FakeController();
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.Zero);
        service.Enable(Target());

        controller.ExternalWrite(new SystemProxySnapshot(true, "127.0.0.1:9999", new[] { "localhost" }));
        SystemProxySnapshot snapshot = service.RefreshState();

        Assert.True(snapshot.Enabled);
        Assert.Equal("127.0.0.1:7890", snapshot.Server);   // 已被修复回期望值
    }

    [Fact]
    public async Task 去抖窗口内多次通知只核对一次()
    {
        var controller = new FakeController();
        // 长（相对测试而言）去抖窗口：多次通知合并
        using var service = new SystemProxyService(controller, debounceInterval: TimeSpan.FromMilliseconds(300));
        service.Enable(Target());
        int enableCallsBefore = controller.EnableCalls;
        controller.ExternalWrite(SystemProxySnapshot.Disabled);

        service.NotifyRegistryChanged();
        service.NotifyRegistryChanged();
        service.NotifyRegistryChanged();
        await Task.Delay(600);

        // 一次合并后的守护回写（而非三次）
        Assert.Equal(enableCallsBefore + 1, controller.EnableCalls);
    }
}
