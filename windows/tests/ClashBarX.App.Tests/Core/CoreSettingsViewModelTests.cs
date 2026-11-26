using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>核心设置区规格测试（源：SystemTabView 核心设置 + 代理端口防抖语义）。</summary>
public class CoreSettingsViewModelTests
{
    private sealed class Harness
    {
        public List<(string Key, object? Value)> Patches = new();
        public List<bool> CloseConnectionsEvents = new();

        public CoreSettingsViewModel ViewModel = null!;

        public Harness()
        {
            ViewModel = new CoreSettingsViewModel
            {
                PatchConfigAsync = (key, value) =>
                {
                    Patches.Add((key, value));
                    return Task.CompletedTask;
                },
                // 测试防抖：立即执行（不延迟）
                DebounceFactory = action =>
                {
                    action();
                    return new NoopDisposable();
                },
            };
            ViewModel.CloseConnectionsChanged += CloseConnectionsEvents.Add;
        }
    }

    private sealed record NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public async Task 核心开关变化触发对应键的PATCH()
    {
        var harness = new Harness();

        harness.ViewModel.AllowLan = true;
        harness.ViewModel.Ipv6 = true;
        harness.ViewModel.TcpConcurrent = true;

        Assert.Equal(3, harness.Patches.Count);
        Assert.Contains(("allow-lan", (object?)true), harness.Patches);
        Assert.Contains(("ipv6", (object?)true), harness.Patches);
        Assert.Contains(("tcp-concurrent", (object?)true), harness.Patches);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 日志等级变化PATCH字符串值()
    {
        var harness = new Harness();

        harness.ViewModel.LogLevel = "warning";

        Assert.Contains(("log-level", (object?)"warning"), harness.Patches);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 端口合法输入经防抖提交()
    {
        var harness = new Harness();

        harness.ViewModel.MixedPortText = "7891";

        Assert.Contains(("mixed-port", (object?)7891), harness.Patches);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 端口非法输入静默忽略()
    {
        var harness = new Harness();

        harness.ViewModel.PortText = "abc";
        harness.ViewModel.SocksPortText = "-5";
        harness.ViewModel.RedirPortText = "70000";

        Assert.DoesNotContain(harness.Patches, patch => patch.Key == "port" || patch.Key == "socks-port" || patch.Key == "redir-port");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 端口0视为关闭该端口()
    {
        var harness = new Harness();

        harness.ViewModel.TproxyPortText = "0";

        Assert.Contains(("tproxy-port", (object?)0), harness.Patches);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 连续输入只提交最后一次()
    {
        var patches = new List<(string Key, object? Value)>();
        var pending = new List<Action>();
        var viewModel = new CoreSettingsViewModel
        {
            PatchConfigAsync = (key, value) =>
            {
                patches.Add((key, value));
                return Task.CompletedTask;
            },
            // 防抖假体：action 挂起不执行，测试端手动放行（对应真实 750ms 窗口）
            DebounceFactory = action =>
            {
                pending.Add(action);
                return new NoopDisposable();
            },
        };

        viewModel.MixedPortText = "7891";
        viewModel.MixedPortText = "7892";
        viewModel.MixedPortText = "7893";

        Assert.Empty(patches);   // 防抖窗口内尚未提交

        // 放行窗口末次（任意一个闭包读的都是最新的待提交值）
        pending[^1]();
        await Task.Yield();

        Assert.Single(patches, patch => patch.Key == "mixed-port");
        Assert.Equal(7893, patches.Single(patch => patch.Key == "mixed-port").Value);
    }

    [Fact]
    public async Task 切换节点关闭连接为本地偏好上抛()
    {
        var harness = new Harness();

        harness.ViewModel.CloseConnections = true;

        Assert.Equal(new[] { true }, harness.CloseConnectionsEvents);
        Assert.Empty(harness.Patches);   // 本地偏好不 PATCH 内核
        await Task.CompletedTask;
    }

    [Fact]
    public void 快照载入不触发PATCH()
    {
        var harness = new Harness();

        harness.ViewModel.ApplySnapshot(
            allowLan: true, ipv6: null, tcpConcurrent: false, logLevel: "error",
            mixedPort: 7890, port: null, socksPort: 1080, redirPort: null, tproxyPort: null);

        Assert.True(harness.ViewModel.AllowLan);
        Assert.Equal("error", harness.ViewModel.LogLevel);
        Assert.Equal("7890", harness.ViewModel.MixedPortText);
        Assert.Equal("1080", harness.ViewModel.SocksPortText);
        Assert.Empty(harness.Patches);
    }

    [Fact]
    public void Restore关连接不触发事件()
    {
        var harness = new Harness();

        harness.ViewModel.RestoreCloseConnections(true);

        Assert.True(harness.ViewModel.CloseConnections);
        Assert.Empty(harness.CloseConnectionsEvents);
    }
}
