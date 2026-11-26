using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClashBarX.App.Core;

namespace ClashBarX.App;

/// <summary>
/// M2 主窗：内核状态、系统代理开关、订阅管理（导入/启用/删除）与运行日志。
/// 业务事件上抛 App 层处理；关闭按钮最小化到托盘（常驻），退出走托盘菜单。
/// </summary>
public partial class MainWindow : Window
{
    private readonly StringBuilder _logBuffer = new(capacity: 4096);
    private bool _suppressToggleEvents;

    /// <summary>用户切换系统代理开关（参数为新状态）。</summary>
    public event Action<bool>? SystemProxyToggled;

    /// <summary>请求导入订阅（参数为 URL 文本）。</summary>
    public event Action<string>? SubscriptionImportRequested;

    /// <summary>请求启用列表中选中的配置（参数为文件名）。</summary>
    public event Action<string>? ConfigActivateRequested;

    /// <summary>请求删除列表中选中的配置（参数为文件名）。</summary>
    public event Action<string>? ConfigRemoveRequested;

    public MainWindow()
    {
        InitializeComponent();
        SystemProxyToggle.Checked += (_, _) => RaiseToggled(true);
        SystemProxyToggle.Unchecked += (_, _) => RaiseToggled(false);
    }

    public void SetCoreStatus(string status) => CoreStatusText.Text = status;

    /// <summary>程序化同步开关勾选态（不回环触发事件）。调用方负责 UI 线程编组。</summary>
    public void SetSystemProxyChecked(bool isChecked)
    {
        _suppressToggleEvents = true;
        try
        {
            SystemProxyToggle.IsChecked = isChecked;
        }
        finally
        {
            _suppressToggleEvents = false;
        }
    }

    /// <summary>刷新配置列表与选中标记。调用方负责 UI 线程编组。</summary>
    public void SetConfigFiles(IReadOnlyList<string> fileNames, string? selected)
    {
        ConfigFilesBox.Items.Clear();
        foreach (string name in fileNames)
        {
            var item = new ListBoxItem
            {
                Content = name == selected ? $"{name}  ✓" : name,
                Tag = name,
            };
            ConfigFilesBox.Items.Add(item);
            if (name == selected)
            {
                ConfigFilesBox.SelectedItem = item;
            }
        }
    }

    /// <summary>导入按钮的可用态（下载期间禁用防重复提交）。</summary>
    public void SetImportBusy(bool busy)
    {
        ImportButton.IsEnabled = !busy;
        ImportButton.Content = busy ? "导入中…" : "导入";
    }

    /// <summary>当前是否停在「代理」tab（驱动代理页的刷新节奏）。</summary>
    public bool IsProxyTabSelected => MainTabs.SelectedIndex == 0;

    /// <summary>当前是否停在「连接」tab（驱动连接流的全量列表处理）。</summary>
    public bool IsConnectionsTabSelected => MainTabs.SelectedIndex == 1;

    /// <summary>
    /// 注入连接页模型并接上悬停暂停重排（源：isReorderingPaused——指针在列表上时
    /// 已显示行保持位置，防止排序跳动导致悬停目标逃跑）。调用方负责 UI 线程编组。
    /// </summary>
    public void SetConnectionsPage(ConnectionsPageViewModel viewModel)
    {
        ConnectionsRoot.DataContext = viewModel;
        ConnectionsList.MouseEnter += (_, _) => viewModel.IsReorderingPaused = true;
        ConnectionsList.MouseLeave += (_, _) => viewModel.IsReorderingPaused = false;
    }

    /// <summary>追加一行运行日志并滚动到底。调用方负责 UI 线程编组。</summary>
    public void AppendLog(string line)
    {
        _logBuffer.AppendLine(line);
        LogTextBox.Text = _logBuffer.ToString();
        LogTextBox.ScrollToEnd();
    }

    private string? SelectedFileName =>
        (ConfigFilesBox.SelectedItem as ListBoxItem)?.Tag as string;

    private void RaiseToggled(bool isChecked)
    {
        if (!_suppressToggleEvents)
        {
            SystemProxyToggled?.Invoke(isChecked);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        string url = SubscriptionUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        SubscriptionImportRequested?.Invoke(url);
    }

    private void OnEnableClick(object sender, RoutedEventArgs e)
    {
        string? fileName = SelectedFileName;
        if (fileName != null)
        {
            ConfigActivateRequested?.Invoke(fileName);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        string? fileName = SelectedFileName;
        if (fileName != null)
        {
            ConfigRemoveRequested?.Invoke(fileName);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗 = 隐藏到托盘，保持常驻；真正的退出在托盘菜单
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
