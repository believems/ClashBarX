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
    public void IsNewerRelease_SemanticComparison(string tag, string current, bool expected)
    {
        Assert.Equal(expected, AppReleaseService.IsNewerRelease(tag, current));
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
