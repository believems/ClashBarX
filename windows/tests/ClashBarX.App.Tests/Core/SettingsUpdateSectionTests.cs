using System.Net.Http;
using ClashBarX.App.Core;
using ClashBarX.Core.Updates;

namespace ClashBarX.App.Tests.Core;

/// <summary>设置页更新检查区块规格（源：availableAppUpdate + footer 交互语义）。</summary>
public class SettingsUpdateSectionTests
{
    private static AppReleaseInfo Release(string tag, bool draft = false, bool prerelease = false) =>
        new(tag, null, $"https://github.com/ClashBarX/releases/tag/{tag}", draft, prerelease);

    [Fact]
    public async Task ManualCheck_NoUpdateReportsLatest()
    {
        var viewModel = new SettingsPageViewModel
        {
            CurrentVersionText = "1.1.0",
            CheckUpdateRequested = () => Task.FromResult<AppReleaseInfo?>(null),
        };

        viewModel.CheckUpdateCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.UpdateStatusText is { Length: > 0 });

        Assert.False(viewModel.HasAvailableUpdate);
        Assert.Equal("已是最新版本", viewModel.UpdateStatusText);
        Assert.False(viewModel.UpdateCheckFailed);
    }

    [Fact]
    public async Task ManualCheck_NewerReleaseShowsVersion()
    {
        var viewModel = new SettingsPageViewModel
        {
            CurrentVersionText = "1.0.0",
            CheckUpdateRequested = () => Task.FromResult<AppReleaseInfo?>(Release("v1.1.0")),
        };

        viewModel.CheckUpdateCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.UpdateStatusText is { Length: > 0 });

        Assert.True(viewModel.HasAvailableUpdate);
        Assert.Equal("1.1.0", viewModel.AvailableUpdateVersionText);
        Assert.Equal("有新版本 1.1.0", viewModel.UpdateStatusText);
    }

    [Fact]
    public async Task ManualCheck_FailureSetsErrorState()
    {
        var viewModel = new SettingsPageViewModel
        {
            CurrentVersionText = "1.0.0",
            CheckUpdateRequested = () => throw new HttpRequestException("offline"),
        };

        viewModel.CheckUpdateCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.UpdateStatusText is { Length: > 0 });

        Assert.True(viewModel.UpdateCheckFailed);
        Assert.Contains("offline", viewModel.UpdateStatusText);
        Assert.False(viewModel.HasAvailableUpdate);
    }

    [Fact]
    public void ApplyLatestRelease_OnlyFillsWhenUpdateAvailable()
    {
        var viewModel = new SettingsPageViewModel { CurrentVersionText = "1.0.0" };

        viewModel.ApplyLatestRelease(Release("v1.1.0"));
        Assert.True(viewModel.HasAvailableUpdate);

        viewModel.ApplyLatestRelease(Release("v1.0.0"));   // 同版本不算更新
        Assert.True(viewModel.HasAvailableUpdate);          // 保留上次结果（源：同结果不重刷）
    }

    [Fact]
    public void ApplyLatestRelease_IgnoresDraftAndPrerelease()
    {
        var viewModel = new SettingsPageViewModel { CurrentVersionText = "1.0.0" };

        viewModel.ApplyLatestRelease(Release("v9.0.0", draft: true));
        Assert.False(viewModel.HasAvailableUpdate);

        viewModel.ApplyLatestRelease(Release("v9.0.0", prerelease: true));
        Assert.False(viewModel.HasAvailableUpdate);
    }

    [Fact]
    public void OpenReleasePage_FavorsUpdateUrlOverIndex()
    {
        var viewModel = new SettingsPageViewModel
        {
            CurrentVersionText = "1.0.0",
        };
        string? opened = null;
        viewModel.OpenReleasePageRequested += url => opened = url;

        viewModel.OpenReleasePage();
        Assert.Equal(SettingsPageViewModel.ReleasesIndexUrl, opened);

        viewModel.CheckUpdateRequested = () => Task.FromResult<AppReleaseInfo?>(Release("v1.1.0"));
        viewModel.CheckUpdateCommand.Execute(null);
        viewModel.ApplyLatestRelease(Release("v1.1.0"));
        viewModel.OpenReleasePage();
        Assert.Equal("https://github.com/ClashBarX/releases/tag/v1.1.0", opened);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
