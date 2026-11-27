using System.Windows;
using ClashBarX.App.Core;
using ClashBarX.Core.L10n;

namespace ClashBarX.App;

/// <summary>
/// 无内核安装引导对话框（源：presentInitialNoCoreSetupGuideIfNeeded / app.core.setup_required）：
/// 说明 no-core 策略与内核获取方式，一键打开内核目录 / 发布页。弹出时机由 FirstRunGuidePolicy 决定。
/// </summary>
public partial class CoreSetupGuideWindow : Window
{
    /// <summary>mihomo 官方发布页（Windows 取文件名含 mihomo-windows-amd64 的 zip）。</summary>
    public const string MihomoReleasesUrl = "https://github.com/MetaCubeX/mihomo/releases";

    public CoreSetupGuideWindow(string coreDirectory, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        CoreDirectoryBox.Text = coreDirectory;
    }

    private void OnOpenCoreDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!ShellNavigator.TryOpenDirectory(CoreDirectoryBox.Text, out string? error))
        {
            ShowStatus(CoreL10n.T("log.override.open_folder_failed", error));
        }
    }

    private void OnOpenReleasePageClick(object sender, RoutedEventArgs e)
    {
        if (!ShellNavigator.TryOpenUrl(MihomoReleasesUrl, out string? error))
        {
            ShowStatus(CoreL10n.T("log.about.open_failed", error));
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
