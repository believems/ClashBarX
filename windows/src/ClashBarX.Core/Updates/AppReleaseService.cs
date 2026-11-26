using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Versioning;

namespace ClashBarX.Core.Updates;

/// <summary>最新发布信息（源：Models/AppRelease.swift 的 AppReleaseInfo）。</summary>
public sealed record AppReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("html_url")] string ReleaseUrl,
    [property: JsonPropertyName("draft")] bool IsDraft,
    [property: JsonPropertyName("prerelease")] bool IsPrerelease)
{
    /// <summary>显示版本（源：displayVersion——tag 归一化去 v 前缀/预发布段）。</summary>
    public string DisplayVersion => AppSemanticVersion.NormalizedDisplayVersion(TagName);
}

/// <summary>
/// 应用更新检查（源：Services/AppReleaseService.swift）：GitHub Releases latest API
/// （8s 超时；Accept/X-GitHub-Api-Version/User-Agent 头与源一致）。
/// </summary>
public sealed class AppReleaseService : IDisposable
{
    /// <summary>GitHub 仓库 API 地址（源：latestReleaseURL）。可注入覆盖用于测试。</summary>
    public const string DefaultLatestReleaseUrl = "https://api.github.com/repos/believems/ClashBarX/releases/latest";

    private readonly HttpClient _client;
    private readonly string _latestReleaseUrl;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public AppReleaseService(HttpMessageHandler? httpMessageHandler = null, string? latestReleaseUrl = null)
    {
        _client = new HttpClient(httpMessageHandler ?? new HttpClientHandler()) { Timeout = Timeout };
        _latestReleaseUrl = latestReleaseUrl ?? DefaultLatestReleaseUrl;
    }

    /// <summary>拉取最新 release；非 2xx / 非响应抛异常（源：invalidResponse/statusCode）。</summary>
    public async Task<AppReleaseInfo> FetchLatestReleaseAsync(
        string currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("User-Agent", $"ClashBar/{currentVersion}");

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API 返回 {(int)response.StatusCode}");
        }

        return await response.Content
            .ReadFromJsonAsync<AppReleaseInfo>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new HttpRequestException("GitHub API 响应为空");
    }

    /// <summary>
    /// 可用更新判定（源：availableAppUpdate）：非 draft 非 prerelease 且版本更新
    /// （版本不可解析时按字符串数值比较兜底——源：isNewerRelease 的 fallback）。
    /// </summary>
    public static AppReleaseInfo? ResolveAvailableUpdate(AppReleaseInfo? latest, string currentVersion)
    {
        if (latest is null || latest.IsDraft || latest.IsPrerelease)
        {
            return null;
        }

        return IsNewerRelease(latest.TagName, currentVersion) ? latest : null;
    }

    /// <summary>版本更新判定（源：AppSemanticVersion.isNewerRelease）。</summary>
    public static bool IsNewerRelease(string tagName, string currentVersion)
    {
        AppSemanticVersion? releaseVersion = AppSemanticVersion.Parse(tagName);
        AppSemanticVersion? current = AppSemanticVersion.Parse(currentVersion);
        if (releaseVersion is { } release && current is { } currentVersionValue)
        {
            return release > currentVersionValue;
        }

        string normalizedRelease = AppSemanticVersion.NormalizedDisplayVersion(tagName);
        string normalizedCurrent = AppSemanticVersion.NormalizedDisplayVersion(currentVersion);
        return CompareNumericStrings(normalizedRelease, normalizedCurrent) > 0;
    }

    /// <summary>数值感知字符串比较（源：compare(options: .numeric) 的兜底路径）。</summary>
    internal static int CompareNumericStrings(string left, string right)
    {
        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length || rightIndex < right.Length)
        {
            if (leftIndex < left.Length && char.IsDigit(left[leftIndex])
                && rightIndex < right.Length && char.IsDigit(right[rightIndex]))
            {
                int leftStart = leftIndex;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                {
                    leftIndex++;
                }

                int rightStart = rightIndex;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                {
                    rightIndex++;
                }

                string leftNumber = left[leftStart..leftIndex].TrimStart('0');
                string rightNumber = right[rightStart..rightIndex].TrimStart('0');
                int lengthCompare = leftNumber.Length.CompareTo(rightNumber.Length);
                if (lengthCompare != 0)
                {
                    return lengthCompare;
                }

                int valueCompare = string.CompareOrdinal(leftNumber, rightNumber);
                if (valueCompare != 0)
                {
                    return valueCompare;
                }

                continue;
            }

            if (leftIndex >= left.Length)
            {
                return -1;
            }

            if (rightIndex >= right.Length)
            {
                return 1;
            }

            int characterCompare = string.CompareOrdinal(
                left[leftIndex].ToString(),
                right[rightIndex].ToString());
            if (characterCompare != 0)
            {
                return characterCompare;
            }

            leftIndex++;
            rightIndex++;
        }

        return 0;
    }

    public void Dispose() => _client.Dispose();
}
