using ClashBarX.Core.L10n;

namespace ClashBarX.App.Core;

/// <summary>
/// 关于对话框的展示文案（纯装配、无 UI 依赖，便于单测）：标签走 XAML 资源，这里只给值。
/// 版本号对齐 macOS footer 的「v%@」口径；内核未运行 / 未选配置给 CoreL10n 兜底文案。
/// 语言由调用方显式传入（同 ReloadLocalizedOptions 的口径），不读写全局语言状态。
/// </summary>
public sealed record AboutInfo(
    string AppVersionText,
    string KernelVersionText,
    string ActiveConfigText,
    string DataDirectory)
{
    public static AboutInfo Create(
        string? appVersion,
        string? kernelVersion,
        bool kernelRunning,
        string? selectedConfig,
        string dataDirectory,
        CoreLanguage language) =>
        new(
            AppVersionText: FormatVersion(appVersion) ?? "—",
            // 停止后保留的旧内核版本号不展示（托盘同样只在运行时显示版本），避免「没在跑却有版本」的误导；
            // 运行中但 /version 尚未回报（API 未就绪）给中性占位
            KernelVersionText: kernelRunning
                ? FormatVersion(kernelVersion) ?? "—"
                : CoreL10n.T(language, "tray.core_status.stopped"),
            ActiveConfigText: selectedConfig is { Length: > 0 } config
                ? config
                : CoreL10n.T(language, "about.active_config.none"),
            DataDirectory: dataDirectory);

    private static string? FormatVersion(string? version) =>
        version is { Length: > 0 } text ? $"v{text}" : null;
}
