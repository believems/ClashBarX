using System.IO;
using System.Text;
using System.Text.Json;

namespace ClashBarX.Core.Subscriptions;

/// <summary>
/// 订阅导入/管理（源：ConfigService.swift 的 ConfigImportService 与 Boss 的 M2 分解）：
/// 下载（UA 携带 clash.meta/版本、5MB 上限双保险）→ Clash 配置校验（proxies 任一键）→
/// 落盘 configs\{host}.yaml（host 优先命名防撞名）→ 原子写与同内容跳过 →
/// 订阅记录/选择/删除持久化。HttpMessageHandler 可注入供单测。
/// </summary>
public sealed class SubscriptionService
{
    /// <summary>单订阅大小上限（源：maxRemoteConfigBytes）。</summary>
    public const int MaxRemoteConfigBytes = 5 * 1024 * 1024;

    /// <summary>下载超时（源：timeoutIntervalForRequest 15s）。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly HttpMessageHandler _handler;
    private readonly string _configsDirectory;
    private readonly string _storeFilePath;

    public SubscriptionService(
        string configsDirectory,
        string storeFilePath,
        HttpMessageHandler? handler = null)
    {
        _configsDirectory = configsDirectory;
        _storeFilePath = storeFilePath;
        _handler = handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        };
    }

    /// <summary>当前订阅记录列表。</summary>
    public IReadOnlyList<SubscriptionInfo> List()
    {
        lock (_gate)
        {
            return LoadStoreNoLock().Subscriptions;
        }
    }

    /// <summary>当前选中的配置文件名（null = 未选择，调用方走默认配置）。</summary>
    public string? GetSelectedFileName()
    {
        lock (_gate)
        {
            return LoadStoreNoLock().SelectedFileName;
        }
    }

    /// <summary>
    /// 导入订阅：下载 → 校验 → 落盘 configs\{host}.yaml → 记录。同 URL 重复导入执行刷新。
    /// </summary>
    public async Task<SubscriptionInfo> AddAsync(
        Uri remoteUrl,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (remoteUrl.Scheme != "http" && remoteUrl.Scheme != "https")
        {
            throw new SubscriptionException("订阅地址必须是 http/https 链接");
        }

        byte[] data = await DownloadRemoteConfigAsync(remoteUrl, userAgent, cancellationToken).ConfigureAwait(false);
        ValidateClashConfig(data);

        string fileName = InferRemoteConfigFileName(remoteUrl);
        bool changed = WriteConfigData(data, Path.Combine(_configsDirectory, fileName));

        lock (_gate)
        {
            SubscriptionStore store = LoadStoreNoLock();
            var existing = store.Subscriptions.FirstOrDefault(item => item.FileName == fileName);
            SubscriptionInfo updated = existing is null
                ? new SubscriptionInfo(remoteUrl.ToString(), fileName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                : existing with
                {
                    Url = remoteUrl.ToString(),
                    LastSuccessAt = DateTimeOffset.UtcNow,
                };
            _ = updated;

            var subscriptions = new List<SubscriptionInfo>();
            foreach (SubscriptionInfo item in store.Subscriptions)
            {
                subscriptions.Add(item.FileName == fileName ? updated : item);
            }

            if (existing is null)
            {
                subscriptions.Add(updated);
            }

            SaveStoreNoLock(new SubscriptionStore(subscriptions, store.SelectedFileName));
            return updated;
        }
    }

    /// <summary>移除订阅：删配置文件与记录；若是当前选中则一并清除选择。</summary>
    public void Remove(string fileName)
    {
        lock (_gate)
        {
            SubscriptionStore store = LoadStoreNoLock();
            SubscriptionInfo? target = store.Subscriptions.FirstOrDefault(item => item.FileName == fileName);
            if (target is null)
            {
                return;
            }

            try
            {
                File.Delete(Path.Combine(_configsDirectory, fileName));
            }
            catch (IOException)
            {
                // 文件缺失/占用不阻断记录清理
            }

            var remaining = store.Subscriptions.Where(item => item.FileName != fileName).ToList();
            string? selected = store.SelectedFileName == fileName ? null : store.SelectedFileName;
            SaveStoreNoLock(new SubscriptionStore(remaining, selected));
        }
    }

    /// <summary>选择配置（供内核下次启动/切换使用）；文件须存在于 configs 目录。</summary>
    public void Select(string fileName)
    {
        lock (_gate)
        {
            if (!File.Exists(Path.Combine(_configsDirectory, fileName)))
            {
                throw new SubscriptionException($"配置文件不存在：{fileName}");
            }

            SubscriptionStore store = LoadStoreNoLock();
            SaveStoreNoLock(new SubscriptionStore(store.Subscriptions, fileName));
        }
    }

    /// <summary>选中配置的完整路径（未选择时 null）。</summary>
    public string? GetSelectedConfigPath()
    {
        string? fileName = GetSelectedFileName();
        if (fileName is null)
        {
            return null;
        }

        string path = Path.Combine(_configsDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>configs 目录下的配置文件清单（*.yaml / *.yml，按文件名排序）。</summary>
    public IReadOnlyList<string> ListConfigFiles()
    {
        if (!Directory.Exists(_configsDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(_configsDirectory)
            .Where(path =>
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                return ext is ".yaml" or ".yml";
            })
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;

    }

    /// <summary>订阅文件名取 host（源：inferredRemoteConfigFileName；「:」替换为「-」防撞名）。</summary>
    internal static string InferRemoteConfigFileName(Uri remoteUrl)
    {
        string host = remoteUrl.Host.Trim();
        // .NET 的 IPv6 host 带方括号（Swift 不带），统一剥掉后再替换冒号
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        if (host.Length == 0)
        {
            return "remote-config.yaml";
        }

        return $"{host.Replace(':', '-')}.yaml";
    }

    /// <summary>
    /// 下载订阅（源：downloadRemoteConfigData）：UA 可注入；Content-Length 预检 + 流式计数双保险。
    /// </summary>
    private async Task<byte[]> DownloadRemoteConfigAsync(
        Uri remoteUrl, string? userAgent, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = DownloadTimeout };
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent.Trim());
        }

        using HttpResponseMessage response = await client
            .GetAsync(remoteUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SubscriptionException($"订阅服务器返回错误（{(int)response.StatusCode}）");
        }

        if (response.Content.Headers.ContentLength is long declared && declared > MaxRemoteConfigBytes)
        {
            throw new SubscriptionException($"订阅内容超出大小上限（{MaxRemoteConfigBytes} 字节）");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRemoteConfigBytes)
            {
                throw new SubscriptionException($"订阅内容超出大小上限（{MaxRemoteConfigBytes} 字节）");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Clash 配置校验（源：validateClashConfigData：UTF-8 且含 proxies 任一键）。</summary>
    internal static void ValidateClashConfig(byte[] data)
    {
        if (data.Length == 0)
        {
            throw new SubscriptionException("订阅响应为空");
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(data);
        }
        catch (ArgumentException)
        {
            throw new SubscriptionException("订阅内容不是有效的 UTF-8 文本");
        }

        bool hasProxies = text.Contains("proxies:")
            || text.Contains("proxy-groups:")
            || text.Contains("proxy-providers:");
        if (!hasProxies)
        {
            string preview = text.Length > 200 ? text[..200] : text;
            throw new SubscriptionException(
                $"订阅内容不是有效的 Clash 配置（缺少 proxies 键）。内容预览：{preview}");
        }
    }

    /// <summary>原子写并做同内容跳过（源：writeConfigData；返回是否实际写入）。</summary>
    internal bool WriteConfigData(byte[] data, string targetPath)
    {
        try
        {
            if (File.Exists(targetPath) && File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(data))
            {
                return false;
            }
        }
        catch (IOException)
        {
            // 读取失败按需要写入处理
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string temporaryPath = targetPath + ".tmp";
        File.WriteAllBytes(temporaryPath, data);
        File.Move(temporaryPath, targetPath, overwrite: true);
        return true;
    }

    private SubscriptionStore LoadStoreNoLock()
    {
        try
        {
            if (!File.Exists(_storeFilePath))
            {
                return SubscriptionStore.Empty;
            }

            SubscriptionStore? store = JsonSerializer.Deserialize<SubscriptionStore>(
                File.ReadAllText(_storeFilePath), SubscriptionStoreJson.Options);
            return store ?? SubscriptionStore.Empty;
        }
        catch (JsonException)
        {
            return SubscriptionStore.Empty;
        }
        catch (IOException)
        {
            return SubscriptionStore.Empty;
        }
    }

    private void SaveStoreNoLock(SubscriptionStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storeFilePath)!);
        File.WriteAllText(
            _storeFilePath,
            JsonSerializer.Serialize(store, SubscriptionStoreJson.Options));
    }
}
