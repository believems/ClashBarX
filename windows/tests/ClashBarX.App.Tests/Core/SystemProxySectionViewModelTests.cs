using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>网络页系统代理区块规格测试（源：SystemTabView 绕过区块 + 快捷开关语义）。</summary>
public class SystemProxySectionViewModelTests
{
    private sealed class Harness
    {
        public int SaveCalls;
        public List<bool> Toggles = new();
        public SystemProxySectionViewModel ViewModel = null!;

        public Harness() => Reset();

        public void Reset() =>
            ViewModel = new SystemProxySectionViewModel(() =>
            {
                SaveCalls++;
                return Task.CompletedTask;
            });
    }

    [Fact]
    public void 默认列表为八条系统例外()
    {
        var harness = new Harness();

        Assert.Equal(8, harness.ViewModel.Exceptions.Count);
        Assert.Contains("127.0.0.1", harness.ViewModel.NormalizedExceptions());
        Assert.Contains("<local>", harness.ViewModel.NormalizedExceptions());
    }

    [Fact]
    public void 开关切换上抛且镜像同步不回环()
    {
        var harness = new Harness();
        harness.ViewModel.ToggleRequested += harness.Toggles.Add;

        harness.ViewModel.IsEnabled = true;
        harness.ViewModel.IsEnabled = false;
        Assert.Equal(new[] { true, false }, harness.Toggles);

        harness.ViewModel.SetEnabled(true);   // 程序化镜像（App 推送）不触发
        Assert.Equal(2, harness.Toggles.Count);
        Assert.True(harness.ViewModel.IsEnabled);
    }

    [Fact]
    public void 新增条目去重并清空输入()
    {
        var harness = new Harness();

        harness.ViewModel.NewException = "  *.example.com  ";
        Assert.True(harness.ViewModel.CanAddException);
        harness.ViewModel.AddException();

        Assert.Contains("*.example.com", harness.ViewModel.NormalizedExceptions());
        Assert.Equal(string.Empty, harness.ViewModel.NewException);   // 输入清空
        Assert.False(harness.ViewModel.CanAddException);
        Assert.True(harness.ViewModel.HasPendingChanges);

        // 忽略大小写去重
        harness.ViewModel.NewException = "*.EXAMPLE.com";
        harness.ViewModel.AddException();
        Assert.Single(harness.ViewModel.NormalizedExceptions(), value => value == "*.example.com");
    }

    [Fact]
    public async Task 保存命令清脏并调用注入函数()
    {
        var harness = new Harness();
        harness.ViewModel.NewException = "*.example.com";
        harness.ViewModel.AddException();
        Assert.True(harness.ViewModel.HasPendingChanges);

        await Task.Run(() => harness.ViewModel.SaveExceptionsCommand.Execute(null));

        Assert.False(harness.ViewModel.HasPendingChanges);
        Assert.Equal(1, harness.SaveCalls);
    }

    [Fact]
    public void 恢复默认重置列表并置脏()
    {
        var harness = new Harness();
        harness.ViewModel.LoadExceptions(new[] { "custom.local" });
        Assert.Single(harness.ViewModel.Exceptions);

        harness.ViewModel.ResetExceptionsCommand.Execute(null);

        Assert.Equal(8, harness.ViewModel.Exceptions.Count);
        Assert.True(harness.ViewModel.HasPendingChanges);
    }

    [Fact]
    public void 载入持久化列表替换默认()
    {
        var harness = new Harness();

        harness.ViewModel.LoadExceptions(new[] { "a.local", "b.local" });

        Assert.Equal(new[] { "a.local", "b.local" }, harness.ViewModel.NormalizedExceptions());
        Assert.False(harness.ViewModel.HasPendingChanges);
    }

    [Fact]
    public void 归一化去空与尾随空白()
    {
        var harness = new Harness();
        harness.ViewModel.LoadExceptions(new[] { "  a.local  ", "", "   " });

        Assert.Equal(new[] { "a.local" }, harness.ViewModel.NormalizedExceptions());
    }

    [Fact]
    public void 行内编辑经包装类可变更()
    {
        var harness = new Harness();
        BypassEntryViewModel entry = harness.ViewModel.Exceptions[0];
        string original = entry.Value;

        entry.Value = "edited.local";

        Assert.Contains("edited.local", harness.ViewModel.NormalizedExceptions());
        Assert.DoesNotContain(original, harness.ViewModel.NormalizedExceptions());
    }
}
