using System.Windows;
using ClashBarX.App.Core;
using ClashBarX.Core.L10n;

namespace ClashBarX.App;

/// <summary>
/// 关于对话框（应用菜单「关于 ClashBarX」的落点）：应用版本、内核版本、生效配置、数据目录。
/// 设置页关于卡 1.0.0 已定稿移除，此前菜单项只剩「跳设置页」成了无效果入口；现改为弹本对话框，
/// 文案装配见 AboutInfo（可单测），发布页链接与维护卡「检查更新」同源（ReleasesIndexUrl）。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow(AboutInfo info, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        AppVersionText.Text = info.AppVersionText;
        KernelVersionText.Text = info.KernelVersionText;
        ActiveConfigText.Text = info.ActiveConfigText;
        DataDirectoryBox.Text = info.DataDirectory;
    }

    private void OnOpenReleasePageClick(object sender, RoutedEventArgs e)
    {
        if (!ShellNavigator.TryOpenUrl(SettingsPageViewModel.ReleasesIndexUrl, out string? error))
        {
            StatusText.Text = CoreL10n.T("log.about.open_failed", error);
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
