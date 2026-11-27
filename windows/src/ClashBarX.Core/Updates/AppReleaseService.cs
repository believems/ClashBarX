using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Versioning;

namespace ClashBarX.Core.Updates;

/// <summary>发布资产（源：Models/AppRelease.swift 的 AppReleaseAsset）。</summary>
public sealed record AppReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

/// <summary>最新发布信息（源：Models/AppRelease.swift 的 AppReleaseInfo）。</summary>
public sealed record AppReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("html_url")] string ReleaseUrl,
    [property: JsonPropertyName("draft")] bool IsDraft,
    [property: JsonPropertyName("prerelease")] bool IsPrerelease,
    [property: JsonPropertyName("assets")] IReadOnlyList<AppReleaseAsset>? Assets = null)
{
    /// <summary>显示版本（源：displayVersion——tag 归一化去平台/ v 前缀与预发布段）。</summary>
    public string DisplayVersion => AppSemanticVersion.NormalizedDisplayVersion(NormalizeTag(TagName));

    /// <summary>
    /// Windows 独立发版 tag（win-v1.2.3 / win-1.2.3，见 release-win.yml）剥掉平台前缀。
    /// 不剥的话语义版本解析会在「-」处截断得到 "win" 而失败，兜底的数值字符串比较又把
    /// "win-…" 排在一切数字版本之后——同版本（win-v1.0.0 对 1.0.0）也会被判成「有新版本」。
    /// </summary>
    internal static string NormalizeTag(string tagName) =>
        tagName.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? tagName[4..] : tagName;

    /// <summary>
    /// 是否含 Windows 可安装资产（.exe 安装包 / .zip 便携包）。双平台共用一条发版线后，
    /// /releases/latest 只按时间取最新稳定版，会把 macOS 的 release（只有 DMG）当成
    /// Windows 的新版本；按资产过滤出 Windows 可用的发版（对齐 macOS 侧 hasMacInstaller）。
    /// </summary>
    public bool HasWindowsInstaller => Assets?.Any(asset => IsWindowsInstallerAsset(asset.Name)) == true;

    /// <summary>sha256 校验文件（*.exe.sha256 / *.zip.sha256）不算安装资产。</summary>
    internal static bool IsWindowsInstallerAsset(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 应用更新检查（源：Services/AppReleaseService.swift）：GitHub Releases API
/// （8s 超时；Accept/X-GitHub-Api-Version/User-Agent 头与源一致）。
/// 应用自身更新走发版列表端点按 Windows 资产过滤（见 FetchLatestWindowsReleaseAsync）；
/// latest 端点仅供不区分平台的仓库（如 mihomo 内核）使用。
/// </summary>
public sealed class AppReleaseService : IDisposable
{
    /// <summary>GitHub 仓库 latest API 地址（源：latestReleaseURL）。可注入覆盖用于测试。</summary>
    public const string DefaultLatestReleaseUrl = "https://api.github.com/repos/believems/ClashBarX/releases/latest";

    /// <summary>
    /// 发版列表地址（对齐 macOS 侧 commit 2f70a67 的 releasesURL）：latest 端点不区分平台，
    /// 这里按 Windows 安装资产过滤出可用的最新稳定发版。
    /// </summary>
    public const string DefaultReleasesListUrl = "https://api.github.com/repos/believems/ClashBarX/releases?per_page=10";

    private readonly HttpClient _client;
    private readonly string _latestReleaseUrl;
    private readonly string _releasesListUrl;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public AppReleaseService(
        HttpMessageHandler? httpMessageHandler = null,
        string? latestReleaseUrl = null,
        string? releasesListUrl = null)
    {
        _client = new HttpClient(httpMessageHandler ?? new HttpClientHandler()) { Timeout = Timeout };
        _latestReleaseUrl = latestReleaseUrl ?? DefaultLatestReleaseUrl;
        _releasesListUrl = releasesListUrl ?? DefaultReleasesListUrl;
    }

    private static HttpRequestMessage CreateRequest(string url, string currentVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 单次探测禁用连接复用：陈旧连接被远端 RST 时连接池后台路径会产生
        // UnobservedTaskException 噪声（真机实测 10054）
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("User-Agent", $"ClashBar/{currentVersion}");
        return request;
    }

    private async Task<T?> GetJsonAsync<T>(string url, string currentVersion, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(url, currentVersion);
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API 返回 {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取 latest release（不区分平台——仅用于 mihomo 内核这类单一产品线）；
    /// 非 2xx / 非响应抛异常（源：invalidResponse/statusCode）。
    /// </summary>
    public async Task<AppReleaseInfo> FetchLatestReleaseAsync(
        string currentVersion, CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync<AppReleaseInfo>(_latestReleaseUrl, currentVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new HttpRequestException("GitHub API 响应为空");
    }

    /// <summary>
    /// 拉取最新可用的 Windows 发版（对齐 macOS 侧 2f70a67）：列表按时间倒序，取首个
    /// 非 draft、非 prerelease 且资产含 .exe / .zip 的发版；没有则抛异常。
    /// </summary>
    public async Task<AppReleaseInfo> FetchLatestWindowsReleaseAsync(
        string currentVersion, CancellationToken cancellationToken = default)
    {
        List<AppReleaseInfo>? releases = await GetJsonAsync<List<AppReleaseInfo>>(
            _releasesListUrl, currentVersion, cancellationToken).ConfigureAwait(false);
        return PickLatestWindowsRelease(releases)
            ?? throw new HttpRequestException("GitHub API 响应中没有含 Windows 安装包的发版");
    }

    /// <summary>平台资产过滤（源：releases.first(where: !isDraft &amp;&amp; !isPrerelease &amp;&amp; hasMacInstaller)）。</summary>
    public static AppReleaseInfo? PickLatestWindowsRelease(IEnumerable<AppReleaseInfo>? releases) =>
        releases?.FirstOrDefault(release => !release.IsDraft && !release.IsPrerelease && release.HasWindowsInstaller);

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

    /// <summary>版本更新判定（源：AppSemanticVersion.isNewerRelease；tag 先剥 Windows 平台前缀）。</summary>
    public static bool IsNewerRelease(string tagName, string currentVersion)
    {
        string normalizedTag = AppReleaseInfo.NormalizeTag(tagName);
        AppSemanticVersion? releaseVersion = AppSemanticVersion.Parse(normalizedTag);
        AppSemanticVersion? current = AppSemanticVersion.Parse(currentVersion);
        if (releaseVersion is { } release && current is { } currentVersionValue)
        {
            return release > currentVersionValue;
        }

        string normalizedRelease = AppSemanticVersion.NormalizedDisplayVersion(normalizedTag);
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
