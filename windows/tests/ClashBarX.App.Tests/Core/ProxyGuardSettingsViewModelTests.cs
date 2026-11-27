using ClashBarX.App.Core;
using ClashBarX.Core.ProxyGuard;

namespace ClashBarX.App.Tests.Core;

/// <summary>代理守护设置区规格测试（源：SystemTabView 守护区块交互语义）。</summary>
public class ProxyGuardSettingsViewModelTests
{
    private sealed class Harness
    {
        public int CheckCalls;
        public bool CanCheck = true;
        public List<bool> Toggles = new();
        public List<int> Intervals = new();
        public ProxyGuardSettingsViewModel ViewModel = null!;

        public Harness()
        {
            ViewModel = new ProxyGuardSettingsViewModel(
                () =>
                {
                    CheckCalls++;
                    return Task.CompletedTask;
                },
                () => CanCheck)
            {
                IntervalText = "300",
            };
            ViewModel.GuardToggled += Toggles.Add;
            ViewModel.IntervalChanged += Intervals.Add;
        }
    }

    [Fact]
    public void 开关切换上抛事件()
    {
        var harness = new Harness();

        harness.ViewModel.IsGuardEnabled = true;
        harness.ViewModel.IsGuardEnabled = false;

        Assert.Equal(new[] { true, false }, harness.Toggles);
    }

    [Fact]
    public void 关闭开关清除结果反馈行()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyProbeState(new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, 2));
        Assert.True(harness.ViewModel.HasOutcome);

        harness.ViewModel.IsGuardEnabled = true;   // 开
        harness.ViewModel.ApplyProbeState(new ProxyGuardProbeState(ProxyGuardProbeOutcome.Reachable, 0));
        Assert.Equal("网络连通正常", harness.ViewModel.OutcomeText);

        harness.ViewModel.IsGuardEnabled = false;   // 关 → 清反馈文案（配置区常驻置灰，结果行留空占位、不折叠）
        Assert.Null(harness.ViewModel.OutcomeText);
        Assert.False(harness.ViewModel.HasOutcome);
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData("86400", 86400)]
    public void 合法周期回写并上抛(string text, int expected)
    {
        var harness = new Harness();

        harness.ViewModel.IntervalText = text;

        Assert.Equal(new[] { expected }, harness.Intervals);
        Assert.Equal(expected.ToString(), harness.ViewModel.IntervalText);
    }

    [Fact]
    public void 相同周期重复输入不重复上抛()
    {
        var harness = new Harness();   // 初始 300

        harness.ViewModel.IntervalText = "300";   // 与当前值相同：幂等，不上抛

        Assert.Empty(harness.Intervals);
        Assert.Equal("300", harness.ViewModel.IntervalText);
    }

    [Theory]
    [InlineData("10")]        // 低于下限 30
    [InlineData("100000")]    // 高于上限 86400
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("30.5")]
    public void 非法周期静默忽略并回弹(string text)
    {
        var harness = new Harness();

        harness.ViewModel.IntervalText = text;

        Assert.Empty(harness.Intervals);   // 源：解析失败静默忽略
        Assert.Equal("300", harness.ViewModel.IntervalText);   // 回弹到当前生效值（源：Binding get 重求值）
    }

    [Fact]
    public void 探测结果两态反馈文案()
    {
        var harness = new Harness();

        harness.ViewModel.ApplyProbeState(new ProxyGuardProbeState(ProxyGuardProbeOutcome.Reachable, 0));
        Assert.Equal("网络连通正常", harness.ViewModel.OutcomeText);
        Assert.Equal("ok", harness.ViewModel.OutcomeLevel);

        harness.ViewModel.ApplyProbeState(new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, 2));
        Assert.Equal("探测失败（连续第 2 次）", harness.ViewModel.OutcomeText);
        Assert.Equal("warning", harness.ViewModel.OutcomeLevel);
    }

    [Fact]
    public async Task 立即检查期间probing状态翻转()
    {
        var probeStarted = new TaskCompletionSource();
        var viewModel = new ProxyGuardSettingsViewModel(
            () => probeStarted.Task,
            () => true)
        {
            IntervalText = "300",
        };

        viewModel.CheckNowCommand.Execute(null);
        Assert.True(viewModel.IsProbing);   // 检查进行中（按钮切「检查中…」）

        probeStarted.SetResult();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (viewModel.IsProbing && watch.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }

        Assert.False(viewModel.IsProbing);
    }

    [Fact]
    public void 不可检查时命令不调用()
    {
        var harness = new Harness();
        harness.CanCheck = false;

        harness.ViewModel.CheckNowCommand.Execute(null);

        Assert.Equal(0, harness.CheckCalls);
    }

    [Fact]
    public void Restore恢复初始态不触发事件()
    {
        var harness = new Harness();

        harness.ViewModel.Restore(enabled: true, intervalSeconds: 60);

        Assert.True(harness.ViewModel.IsGuardEnabled);
        Assert.Equal("60", harness.ViewModel.IntervalText);
        Assert.Empty(harness.Toggles);
        Assert.Empty(harness.Intervals);
    }
}
