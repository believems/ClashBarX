namespace ClashBarX.Core.WebUI;

/// <summary>
/// 控制器 WebUI 地址构建（源：AppViewModel+Proxy.swift 的 makeControllerUIURL /
/// configuredControllerUIURL / metaCubeXDSetupURL）：
/// 已配置 external-ui 时指向 {controller}/ui[/{name}]；
/// 否则指向 metacubexd 的 setup 引导页（带 controller 连接参数）。
/// </summary>
public static class ControllerWebUiUrl
{
    /// <summary>未配置 external-ui 时的引导页（源：metaCubeXDSetupURL）。</summary>
    public const string MetaCubeXdSetupBaseUrl = "https://metacubex.github.io/metacubexd/";

    /// <summary>构建 WebUI 入口地址。</summary>
    public static string Build(
        string controller,
        string? secret,
        bool externalUiConfigured,
        string? externalUiName)
    {
        return externalUiConfigured
            ? ConfiguredUiUrl(controller, externalUiName)
            : MetaCubeXdSetupUrl(controller, secret);
    }

    /// <summary>{controller}/ui[/{name}]（源：configuredControllerUIURL——path 归一 /ui 起头）。</summary>
    internal static string ConfiguredUiUrl(string controller, string? externalUiName)
    {
        string baseAddress = NormalizeControllerAddress(controller);
        string path = "/ui";
        if (!string.IsNullOrWhiteSpace(externalUiName))
        {
            path += "/" + Uri.EscapeDataString(externalUiName.Trim());
        }

        return baseAddress + path;
    }

    /// <summary>metacubexd setup 引导（源：metaCubeXDSetupURL——fragment 携带连接参数）。</summary>
    internal static string MetaCubeXdSetupUrl(string controller, string? secret)
    {
        string baseAddress = NormalizeControllerAddress(controller);
        bool usesHttps = baseAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        string host = "127.0.0.1";
        int port = usesHttps ? 443 : 80;

        if (Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri? uri))
        {
            if (!string.IsNullOrWhiteSpace(uri.Host))
            {
                host = uri.Host;
            }

            if (uri.Port > 0)
            {
                port = uri.Port;
            }
        }

        string fragment = $"/setup?http={(usesHttps ? "false" : "true")}"
            + $"&hostname={Uri.EscapeDataString(host)}"
            + $"&port={port}"
            + $"&secret={Uri.EscapeDataString(secret?.Trim() ?? string.Empty)}";
        return $"{MetaCubeXdSetupBaseUrl}#{fragment}";
    }

    /// <summary>controller 地址归一（源：normalizedControllerAddress——无 scheme 补 http）。</summary>
    internal static string NormalizeControllerAddress(string controller)
    {
        string trimmed = controller.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"http://{trimmed}";
    }
}
