using System.Net;
using ClashBarX.Core.Tests.TestInfrastructure;
using ClashBarX.Core.Updates;

namespace ClashBarX.Core.Tests.Updates;

/// <summary>应用更新检查规格测试（源：AppReleaseService.swift + availableAppUpdate 语义）。</summary>
public class AppReleaseServiceTests
{
    private const string ReleaseJson = """
        {
            "tag_name": "v1.1.0",
            "name": "ClashBarX v1.1.0",
            "html_url": "https://github.com/believems/ClashBarX/releases/tag/v1.1.0",
            "draft": false,
            "prerelease": false
        }
        """;

    [Fact]
    public async Task FetchLatestRelease_ParsesJson()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson(ReleaseJson);
        using var service = new AppReleaseService(handler);

        AppReleaseInfo release = await service.FetchLatestReleaseAsync("1.0.0");

        Assert.Equal("v1.1.0", release.TagName);
        Assert.Equal("https://github.com/believems/ClashBarX/releases/tag/v1.1.0", release.ReleaseUrl);
        Assert.False(release.IsDraft);
        Assert.False(release.IsPrerelease);
        Assert.Equal("1.1.0", release.DisplayVersion);
    }

    [Fact]
    public async Task FetchLatestRelease_SendsSourceHeaders()
    {
        string? accept = null;
        string? apiVersion = null;
        string? userAgent = null;
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Handler: (request, _) =>
            {
                accept = request.Headers.Accept.FirstOrDefault()?.ToString();
                apiVersion = request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
                    ? string.Join(",", values)
                    : null;
                userAgent = request.Headers.UserAgent.ToString();
                return Task.FromResult(StubHttpMessageHandler.Json(ReleaseJson));
            }));
        using var service = new AppReleaseService(handler);

        await service.FetchLatestReleaseAsync("1.0.0");

        Assert.Equal("application/vnd.github+json", accept);
        Assert.Equal("2022-11-28", apiVersion);
        Assert.Equal("ClashBar/1.0.0", userAgent);
    }

    [Fact]
    public async Task FetchLatestRelease_NonSuccessThrows()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson("""{"message":"rate limited"}""", HttpStatusCode.Forbidden);
        using var service = new AppReleaseService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchLatestReleaseAsync("1.0.0"));
    }

    private const string DownloadBase = "https://github.com/believems/ClashBarX/releases/download";

    private static string ReleaseListJson(params (string Tag, bool Draft, bool Prerelease, string[] Assets)[] releases)
    {
        var items = new List<string>();
        foreach ((string tag, bool draft, bool prerelease, string[] assets) in releases)
        {
            string assetItems = string.Join(",", assets.Select(asset =>
                $"{{ \"name\": \"{asset}\", \"browser_download_url\": \"{DownloadBase}/{tag}/{asset}\" }}"));
            items.Add(
                $$"""
                  {
                    "tag_name": "{{tag}}",
                    "name": "ClashBarX {{tag}}",
                    "html_url": "https://github.com/believems/ClashBarX/releases/tag/{{tag}}",
                    "draft": {{(draft ? "true" : "false")}},
                    "prerelease": {{(prerelease ? "true" : "false")}},
                    "assets": [{{assetItems}}]
                  }
                  """);
        }

        return "[" + string.Join(",", items) + "]";
    }

    private static readonly (string Tag, bool Draft, bool Prerelease, string[] Assets)[] LiveReleaseShape =
    {
        ("v1.0.1", false, false, new[] { "ClashBarX-1.0.1-apple-silicon.dmg", "ClashBarX-1.0.1-apple-silicon.dmg.sha256" }),
        ("win-v1.0.1", false, true, new[] { "ClashBarX-1.0.1-setup.exe" }),
        ("win-v1.0.2", true, false, new[] { "ClashBarX-1.0.2-setup.exe" }),
        ("win-v1.0.0", false, false, new[]
        {
            "ClashBarX-1.0.0-portable-win-x64.zip",
            "ClashBarX-1.0.0-portable-win-x64.zip.sha256",
            "ClashBarX-1.0.0-setup.exe",
            "ClashBarX-1.0.0-setup.exe.sha256",
        }),
    };

    [Fact]
    public async Task FetchLatestWindowsRelease_SkipsMacOnlyPrereleaseAndDraft()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson(ReleaseListJson(LiveReleaseShape));
        using var service = new AppReleaseService(handler);

        AppReleaseInfo release = await service.FetchLatestWindowsReleaseAsync("1.0.0");

        Assert.Equal("win-v1.0.0", release.TagName);
        Assert.Equal("1.0.0", release.DisplayVersion);
        Assert.Equal("https://github.com/believems/ClashBarX/releases/tag/win-v1.0.0", release.ReleaseUrl);
        Assert.Equal(4, release.Assets!.Count);
        Assert.Equal(AppReleaseService.DefaultReleasesListUrl, handler.Requests.Single().Url);
    }

    [Fact]
    public async Task FetchLatestWindowsRelease_SendsSourceHeaders()
    {
        string? accept = null;
        string? apiVersion = null;
        string? userAgent = null;
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Handler: (request, _) =>
            {
                accept = request.Headers.Accept.FirstOrDefault()?.ToString();
                apiVersion = request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
                    ? string.Join(",", values)
                    : null;
                userAgent = request.Headers.UserAgent.ToString();
                return Task.FromResult(StubHttpMessageHandler.Json(ReleaseListJson(LiveReleaseShape)));
            }));
        using var service = new AppReleaseService(handler);

        await service.FetchLatestWindowsReleaseAsync("1.0.0");

        Assert.Equal("application/vnd.github+json", accept);
        Assert.Equal("2022-11-28", apiVersion);
        Assert.Equal("ClashBar/1.0.0", userAgent);
    }

    [Fact]
    public async Task FetchLatestWindowsRelease_ThrowsWhenOnlyOtherPlatformReleased()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson(ReleaseListJson(LiveReleaseShape[0]));
        using var service = new AppReleaseService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchLatestWindowsReleaseAsync("1.0.0"));
    }

    [Fact]
    public async Task FetchLatestWindowsRelease_NonSuccessThrows()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson("""{"message":"rate limited"}""", HttpStatusCode.Forbidden);
        using var service = new AppReleaseService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchLatestWindowsReleaseAsync("1.0.0"));
    }

    [Fact]
    public async Task FetchLatestWindowsRelease_LatestEndpointStaysPlatformAgnostic()
    {
        // 内核更新检查仍走 latest 端点（mihomo 单一产品线，每个发版都含全平台资产）
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson(ReleaseJson);
        using var service = new AppReleaseService(handler);

        AppReleaseInfo release = await service.FetchLatestReleaseAsync("1.0.0");

        Assert.Equal("v1.1.0", release.TagName);
        Assert.Null(release.Assets);
        Assert.False(release.HasWindowsInstaller);
        Assert.Equal(AppReleaseService.DefaultLatestReleaseUrl, handler.Requests.Single().Url);
    }

    [Fact]
    public void HasWindowsInstaller_IgnoresHashSidecarFiles()
    {
        var sidecarOnly = new AppReleaseInfo("win-v9.9.9", null, "https://example.com", false, false,
            new[]
            {
                new AppReleaseAsset("ClashBarX-9.9.9-setup.exe.sha256", "https://example.com/a"),
                new AppReleaseAsset("ClashBarX-9.9.9-portable-win-x64.zip.sha256", "https://example.com/b"),
            });

        Assert.False(sidecarOnly.HasWindowsInstaller);
        Assert.Null(AppReleaseService.PickLatestWindowsRelease(new[] { sidecarOnly }));
    }

    [Theory]
    [InlineData("ClashBarX-1.0.0-setup.exe")]
    [InlineData("ClashBarX-1.0.0-portable-win-x64.zip")]
    [InlineData("ClashBarX-1.0.0-setup-lite.exe")]
    [InlineData("ClashBarX-1.0.0-SETUP.EXE")]
    [InlineData("ClashBarX-1.0.0-portable-win-x64.ZIP")]
    public void HasWindowsInstaller_AcceptsExeAndZipAnyCase(string assetName)
    {
        var release = new AppReleaseInfo("win-v1.0.0", null, "https://example.com", false, false,
            new[] { new AppReleaseAsset(assetName, "https://example.com/a") });

        Assert.True(release.HasWindowsInstaller);
    }

    [Fact]
    public void PickLatestWindowsRelease_TakesFirstQualifyingInListOrder()
    {
        var older = new AppReleaseInfo("win-v1.0.0", null, "https://example.com", false, false,
            new[] { new AppReleaseAsset("ClashBarX-1.0.0-setup.exe", "https://example.com/a") });
        var newer = older with { TagName = "win-v1.0.1" };

        Assert.Null(AppReleaseService.PickLatestWindowsRelease(null));
        Assert.Null(AppReleaseService.PickLatestWindowsRelease(Array.Empty<AppReleaseInfo>()));
        Assert.Same(older, AppReleaseService.PickLatestWindowsRelease(new[] { older, newer }));   // 信任列表序，不自行比版本
        Assert.Same(newer, AppReleaseService.PickLatestWindowsRelease(new[] { older, newer }.Reverse()));
    }

    [Fact]
    public void ResolveAvailableUpdate_RequiresNewerStableRelease()
    {
        AppReleaseInfo stable = new("v1.1.0", null, "https://example.com", false, false);
        AppReleaseInfo draft = new("v9.0.0", null, "https://example.com", true, false);
        AppReleaseInfo prerelease = new("v9.0.0", null, "https://example.com", false, true);

        Assert.Null(AppReleaseService.ResolveAvailableUpdate(null, "1.0.0"));
        Assert.Null(AppReleaseService.ResolveAvailableUpdate(draft, "1.0.0"));
        Assert.Null(AppReleaseService.ResolveAvailableUpdate(prerelease, "1.0.0"));
        Assert.Null(AppReleaseService.ResolveAvailableUpdate(stable, "1.1.0"));
        Assert.Null(AppReleaseService.ResolveAvailableUpdate(stable, "2.0.0"));
        Assert.NotNull(AppReleaseService.ResolveAvailableUpdate(stable, "1.0.0"));
    }

    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("v1.0.0-rc1", "1.0.0", false)]      // 预发布剥离 → 相等
    [InlineData("v1.1.0+build", "1.0.0", true)]     // metadata 剥离
    // Windows 独立发版 tag：剥平台前缀后比较（修复前 win-v1.0.0 对 1.0.0 被判成有新版本）
    [InlineData("win-v1.0.0", "1.0.0", false)]
    [InlineData("win-1.0.0", "1.0.0", false)]
    [InlineData("win-v1.0.1", "1.0.0", true)]
    [InlineData("win-v1.0.0", "1.0.1", false)]
    [InlineData("WIN-V1.0.1", "1.0.0", true)]
    public void IsNewerRelease_SemanticComparison(string tag, string current, bool expected)
    {
        Assert.Equal(expected, AppReleaseService.IsNewerRelease(tag, current));
    }

    [Theory]
    [InlineData("win-v1.0.0", "1.0.0")]
    [InlineData("win-1.2.3", "1.2.3")]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    public void DisplayVersion_StripsWindowsTagPrefix(string tag, string expected)
    {
        Assert.Equal(expected, new AppReleaseInfo(tag, null, "https://example.com", false, false).DisplayVersion);
    }

    [Fact]
    public void ResolveAvailableUpdate_SameWindowsTagVersionIsNotAnUpdate()
    {
        var sameVersion = new AppReleaseInfo("win-v1.0.0", null, "https://example.com", false, false);

        Assert.Null(AppReleaseService.ResolveAvailableUpdate(sameVersion, "1.0.0"));
        Assert.NotNull(AppReleaseService.ResolveAvailableUpdate(sameVersion with { TagName = "win-v1.0.1" }, "1.0.0"));
    }

    [Theory]
    [InlineData("v10", "v9", 1)]        // 数值比较：10 > 9
    [InlineData("v9", "v10", -1)]
    [InlineData("v1.2", "v1.10", -1)]   // 2 < 10（数值而非字典序）
    [InlineData("v1.2", "v1.2", 0)]
    [InlineData("abc", "abc", 0)]
    public void CompareNumericStrings_MatchesSwiftNumericCompare(string left, string right, int expectedSign)
    {
        int actual = AppReleaseService.CompareNumericStrings(left, right);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(actual));
    }
}
