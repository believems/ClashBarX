using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>设置页与开机自启规格测试（源：AppViewModel+Settings / LaunchAtLoginService.swift）。</summary>
public class SettingsPageViewModelTests
{
    private sealed class Harness
    {
        public List<AppAppearanceMode> Appearances = new();
        public List<AppLanguage> Languages = new();
        public List<bool> TrafficOverviews = new();
        public List<bool> LaunchWrites = new();
        public bool FailLaunchWrite;

        public SettingsPageViewModel ViewModel = null!;

        public Harness() => Reset();

        public void Reset()
        {
            ViewModel = new SettingsPageViewModel
            {
                LaunchAtLoginRequested = enabled =>
                {
                    if (FailLaunchWrite)
                    {
                        throw new InvalidOperationException("registry denied");
                    }

                    LaunchWrites.Add(enabled);
                    return Task.CompletedTask;
                },
            };
            ViewModel.AppearanceChanged += Appearances.Add;
            ViewModel.LanguageChanged += Languages.Add;
            ViewModel.TrafficOverviewChanged += TrafficOverviews.Add;
        }
    }

    [Fact]
    public void 外观与语言变化上抛事件()
    {
        var harness = new Harness();

        harness.ViewModel.AppearanceMode = AppAppearanceMode.Dark;
        harness.ViewModel.Language = AppLanguage.English;

        Assert.Equal(new[] { AppAppearanceMode.Dark }, harness.Appearances);
        Assert.Equal(new[] { AppLanguage.English }, harness.Languages);
    }

    [Fact]
    public async Task 自启开关调用写入委托()
    {
        var harness = new Harness();

        await Task.Run(() => harness.ViewModel.LaunchAtLogin = true);

        Assert.Equal(new[] { true }, harness.LaunchWrites);
        Assert.True(harness.ViewModel.LaunchAtLogin);
        Assert.False(harness.ViewModel.LaunchAtLoginFailed);
    }

    [Fact]
    public async Task 自启写入失败回滚开关并置错误提示()
    {
        var harness = new Harness();
        harness.FailLaunchWrite = true;

        await Task.Run(() => harness.ViewModel.LaunchAtLogin = true);

        Assert.False(harness.ViewModel.LaunchAtLogin);   // 回滚
        Assert.True(harness.ViewModel.LaunchAtLoginFailed);
    }

    [Fact]
    public void 流量概览开关上抛()
    {
        var harness = new Harness();

        harness.ViewModel.ShowTrafficOverview = false;

        Assert.Equal(new[] { false }, harness.TrafficOverviews);
    }

    [Fact]
    public async Task 维护动作成功反馈状态行()
    {
        var viewModel = new SettingsPageViewModel
        {
            UpgradeGeoRequested = () => Task.FromResult("GEO 数据更新：success"),
        };

        await Task.Run(() => viewModel.UpgradeGeoCommand.Execute(null));
        await Task.Delay(50);

        Assert.Equal("GEO 数据更新：success", viewModel.MaintenanceStatusText);
        Assert.Equal("ok", viewModel.MaintenanceStatusLevel);
        Assert.False(viewModel.IsMaintaining);
    }

    [Fact]
    public async Task 维护动作异常反馈失败状态()
    {
        var viewModel = new SettingsPageViewModel
        {
            UpgradeCoreRequested = () => throw new InvalidOperationException("文件被锁"),
        };

        await Task.Run(() => viewModel.UpgradeCoreCommand.Execute(null));
        await Task.Delay(50);

        Assert.StartsWith("失败：", viewModel.MaintenanceStatusText);
        Assert.Contains("文件被锁", viewModel.MaintenanceStatusText!);
        Assert.Equal("error", viewModel.MaintenanceStatusLevel);
        Assert.False(viewModel.IsMaintaining);
    }

    [Fact]
    public async Task 升级内核进行中时GEO按钮不联动且可独立执行()
    {
        var coreUpgrade = new TaskCompletionSource<string>();
        var geoUpdate = new TaskCompletionSource<string>();
        int geoCalls = 0;
        var viewModel = new SettingsPageViewModel
        {
            UpgradeCoreRequested = () => coreUpgrade.Task,
            UpgradeGeoRequested = () =>
            {
                geoCalls++;
                return geoUpdate.Task;
            },
        };

        viewModel.UpgradeCoreCommand.Execute(null);
        Assert.True(viewModel.IsUpgradingCore);
        Assert.False(viewModel.IsUpdatingGeo);   // 修复前：共用总 busy 开关，GEO 按钮也跟着变「执行中…」
        Assert.True(viewModel.IsMaintaining);

        viewModel.UpgradeGeoCommand.Execute(null);   // 内核升级期间 GEO 仍可执行（修复前被静默忽略）
        await Task.Delay(20);
        Assert.Equal(1, geoCalls);
        Assert.True(viewModel.IsUpgradingCore);
        Assert.True(viewModel.IsUpdatingGeo);

        geoUpdate.SetResult("GEO 数据更新：success");
        await Task.Delay(50);
        Assert.False(viewModel.IsUpdatingGeo);
        Assert.True(viewModel.IsUpgradingCore);       // 内核升级仍在进行，不因 GEO 完成而复位
        Assert.True(viewModel.IsMaintaining);

        coreUpgrade.SetResult("内核升级：success");
        await Task.Delay(50);
        Assert.False(viewModel.IsUpgradingCore);
        Assert.False(viewModel.IsMaintaining);
    }

    [Fact]
    public async Task 同一维护动作进行中重复触发被忽略()
    {
        var pending = new TaskCompletionSource<string>();
        int calls = 0;
        var viewModel = new SettingsPageViewModel
        {
            UpgradeCoreRequested = () =>
            {
                calls++;
                return pending.Task;
            },
        };

        viewModel.UpgradeCoreCommand.Execute(null);
        viewModel.UpgradeCoreCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(1, calls);

        pending.SetResult("内核升级：success");
        await Task.Delay(50);
        Assert.Equal(1, calls);
        Assert.False(viewModel.IsBusy(MaintenanceAction.UpgradeCore));
    }

    [Fact]
    public void 维护动作进行中只通知本动作属性与汇总态()
    {
        var viewModel = new SettingsPageViewModel
        {
            UpgradeGeoRequested = () => new TaskCompletionSource<string>().Task,
        };
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.UpgradeGeoCommand.Execute(null);

        Assert.Contains(nameof(SettingsPageViewModel.IsUpdatingGeo), changed);
        Assert.Contains(nameof(SettingsPageViewModel.IsMaintaining), changed);
        Assert.DoesNotContain(nameof(SettingsPageViewModel.IsUpgradingCore), changed);
        Assert.DoesNotContain(nameof(SettingsPageViewModel.IsFlushingFakeIp), changed);
        Assert.DoesNotContain(nameof(SettingsPageViewModel.IsFlushingDns), changed);
    }

    [Fact]
    public void Restore恢复初始态不触发事件()
    {
        var harness = new Harness();

        harness.ViewModel.Restore(
            AppAppearanceMode.Dark, AppLanguage.English, launchAtLogin: true, showTrafficOverview: false);

        Assert.Equal(AppAppearanceMode.Dark, harness.ViewModel.AppearanceMode);
        Assert.Equal(AppLanguage.English, harness.ViewModel.Language);
        Assert.True(harness.ViewModel.LaunchAtLogin);
        Assert.False(harness.ViewModel.ShowTrafficOverview);
        Assert.Empty(harness.Appearances);
        Assert.Empty(harness.Languages);
        Assert.Empty(harness.LaunchWrites);
    }
}

/// <summary>开机自启注册表服务测试（随机键名隔离，不动真实 ClashBarX 键）。</summary>
public class LaunchAtLoginServiceTests
{
    private readonly LaunchAtLoginService _service =
        new($"clashbarx-test-{Guid.NewGuid():N}");

    [Fact]
    public void 写入与移除()
    {
        string exePath = Environment.ProcessPath ?? "C:\\app.exe";

        Assert.False(_service.IsEnabled());

        _service.Apply(true, exePath);
        Assert.True(_service.IsEnabled());

        _service.Apply(false, exePath);
        Assert.False(_service.IsEnabled());
    }

    [Fact]
    public void 路径含空格时引号包裹()
    {
        string exePath = "C:\\Program Files\\ClashBarX\\ClashBarX.exe";

        _service.SetEnabled(true, exePath);
        Assert.True(_service.IsEnabled());
        _service.SetEnabled(false, exePath);   // 清理
    }
}
