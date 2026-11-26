using System.IO;
using ClashBarX.App.Core;
using ClashBarX.Core.Overrides;

namespace ClashBarX.App.Tests.Core;

/// <summary>规则覆写区模型规格测试（源：RuleOverrideStore + OverrideTabView 交互）。</summary>
public class RuleOverrideViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"clashbarx-override-vm-{Guid.NewGuid():N}");

    private readonly List<string> _logs = new();
    private readonly List<string> _appliedConfigs = new();

    private RuleOverrideViewModel CreateViewModel(out RuleOverrideService service, bool coreRunning = true)
    {
        service = new RuleOverrideService(
            Path.Combine(_root, "overrides"), Path.Combine(_root, "runtime"));
        return new RuleOverrideViewModel(
            service,
            () =>
            {
                _appliedConfigs.Add("apply");
                return Task.CompletedTask;
            },
            () => coreRunning,
            message => _logs.Add(message),
            () => _logs.Add("open-folder"));
    }

    private void WriteFile(RuleOverrideService service, RuleOverrideFile file, string text) =>
        service.Write(text, file);

    [Fact]
    public void 初始化载入文件计数与草稿()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out RuleOverrideService service);
        WriteFile(service, RuleOverrideFile.Prepend, "DOMAIN,a.com,DIRECT\n# 注释\n");
        WriteFile(service, RuleOverrideFile.Append, "GEOIP,lan,DIRECT\n");

        viewModel.ReloadFromFilesystem();

        Assert.Equal(1, viewModel.Counts.Prepend);
        Assert.Equal(1, viewModel.Counts.Append);
        Assert.Equal(0, viewModel.Counts.RuleProviders);
    }

    [Fact]
    public void 文件区展开互斥并载入草稿()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out RuleOverrideService service);
        WriteFile(service, RuleOverrideFile.Prepend, "DOMAIN,a.com,DIRECT\n");
        viewModel.ReloadFromFilesystem();

        viewModel.ToggleFile(RuleOverrideFile.Prepend);
        Assert.True(viewModel.IsPrependExpanded);
        Assert.True(viewModel.HasExpandedFile);
        Assert.Contains("DOMAIN,a.com,DIRECT", viewModel.CurrentDraft);

        // 展开另一个 → 前一个收起
        viewModel.ToggleFile(RuleOverrideFile.Append);
        Assert.False(viewModel.IsPrependExpanded);
        Assert.True(viewModel.IsAppendExpanded);

        // 再点同一个 → 收起
        viewModel.ToggleFile(RuleOverrideFile.Append);
        Assert.Null(viewModel.ExpandedFile);
        Assert.False(viewModel.HasExpandedFile);
    }

    [Fact]
    public async Task 草稿编辑置脏_保存落盘清脏并触发应用()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out RuleOverrideService service);
        WriteFile(service, RuleOverrideFile.Prepend, "DOMAIN,a.com,DIRECT\n");
        viewModel.ReloadFromFilesystem();
        viewModel.ToggleFile(RuleOverrideFile.Prepend);

        viewModel.CurrentDraft = "DOMAIN,a.com,DIRECT\nDOMAIN,b.com,DIRECT\n";
        Assert.True(viewModel.IsDirty);

        await Task.Run(() => viewModel.SaveCommand.Execute(null));

        Assert.False(viewModel.IsDirty);
        Assert.Equal(2, viewModel.Counts.Prepend);   // 计数刷新
        Assert.Contains("b.com", File.ReadAllText(service.FilePath(RuleOverrideFile.Prepend)));
        Assert.Contains("apply", _appliedConfigs);   // 保存后触发应用
    }

    [Fact]
    public void 还原草稿回到已保存文本()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out RuleOverrideService service);
        WriteFile(service, RuleOverrideFile.Prepend, "DOMAIN,a.com,DIRECT\n");
        viewModel.ReloadFromFilesystem();
        viewModel.ToggleFile(RuleOverrideFile.Prepend);

        viewModel.CurrentDraft = "胡乱输入";
        Assert.True(viewModel.IsDirty);

        viewModel.RevertCommand.Execute(null);

        Assert.False(viewModel.IsDirty);
        Assert.Contains("DOMAIN,a.com,DIRECT", viewModel.CurrentDraft);
    }

    [Fact]
    public void 开关切换上抛事件且关闭清状态行()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out _);
        var toggles = new List<bool>();
        viewModel.EnabledToggled += toggles.Add;

        viewModel.MarkApplied("config.yaml", new RuleOverrideCounts(1, 0, 0));
        Assert.True(viewModel.HasStatus);

        viewModel.IsEnabled = true;
        viewModel.IsEnabled = false;

        Assert.Equal(new[] { true, false }, toggles);
        Assert.False(viewModel.HasStatus);   // 关闭收起状态行
        Assert.Null(viewModel.StatusText);
    }

    [Fact]
    public void 状态行四态文案()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out _);

        viewModel.MarkApplied("config.yaml", new RuleOverrideCounts(1, 2, 0));
        Assert.Contains("已应用到 config.yaml", viewModel.StatusText);
        Assert.Contains("前置 1", viewModel.StatusText!);
        Assert.Equal("ok", viewModel.StatusLevel);

        viewModel.MarkFailed("超时");
        Assert.Equal("应用失败：超时", viewModel.StatusText);
        Assert.Equal("error", viewModel.StatusLevel);

        viewModel.MarkEmpty();
        Assert.Equal("未配置覆写规则", viewModel.StatusText);

        viewModel.MarkPending();
        Assert.Equal("下次启动或重载配置时生效", viewModel.StatusText);
    }

    [Fact]
    public void Restore恢复开关态不触发事件()
    {
        RuleOverrideViewModel viewModel = CreateViewModel(out _);
        var toggles = new List<bool>();
        viewModel.EnabledToggled += toggles.Add;

        viewModel.Restore(enabled: true);

        Assert.True(viewModel.IsEnabled);
        Assert.Empty(toggles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
