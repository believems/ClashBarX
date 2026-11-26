using System.Windows;
using System.Windows.Controls;
using ClashBarX.App.Core;

namespace ClashBarX.App;

/// <summary>
/// 主窗：内核状态、系统代理开关、订阅管理（导入/启用/删除）、连接页与日志页。
/// 业务事件上抛 App 层处理；关闭按钮最小化到托盘（常驻），退出走托盘菜单。
/// </summary>
public partial class MainWindow : Window
{
    private LogsPageViewModel? _logsPage;

    /// <summary>请求导入订阅（参数为 URL 文本）。</summary>
    public event Action<string>? SubscriptionImportRequested;

    /// <summary>请求启用列表中选中的配置（参数为文件名）。</summary>
    public event Action<string>? ConfigActivateRequested;

    /// <summary>请求删除列表中选中的配置（参数为文件名）。</summary>
    public event Action<string>? ConfigRemoveRequested;

    /// <summary>手动内核生命周期请求（源：菜单栏头部启动/停止/重启动作）。</summary>
    public event Action? CoreStartRequested;

    public event Action? CoreStopRequested;

    public event Action? CoreRestartRequested;

    /// <summary>请求更新选中订阅 / 全部订阅（参数为文件名；全部时为空串）。</summary>
    public event Action<string>? SubscriptionRefreshRequested;

    public event Action? SubscriptionRefreshAllRequested;

    /// <summary>请求复制终端代理命令。</summary>
    public event Action? CopyProxyCommandRequested;

    /// <summary>用户请求切换机器目标（null = 本机；远程需 App 层验证连通性后生效）。</summary>
    public event Action<Guid?>? MachineTargetSwitchRequested;

    /// <summary>用户请求打开远程机器管理窗。</summary>
    public event Action? ManageMachinesRequested;

    public MainWindow()
    {
        InitializeComponent();
        MainTabs.SelectionChanged += (_, _) => TabSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>主 tab 选择变化（App 层在 UI 线程上缓存各页可见性，供后台流线程读取）。</summary>
    public event EventHandler? TabSelectionChanged;

    public void SetCoreStatus(string status) => CoreStatusText.Text = status;

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

    /// <summary>当前是否停在「规则」tab（驱动规则页的低频轮询）。</summary>
    public bool IsRulesTabSelected => MainTabs.SelectedIndex == 1;

    /// <summary>当前是否停在「连接」tab（驱动连接流的全量列表处理）。</summary>
    public bool IsConnectionsTabSelected => MainTabs.SelectedIndex == 2;

    /// <summary>
    /// 注入连接页模型并接上悬停暂停重排（源：isReorderingPaused——指针在列表上时
    /// 已显示行保持位置，防止排序跳动导致悬停目标逃跑）。调用方负责 UI 线程编组。
    /// </summary>
    /// <summary>注入规则页模型。调用方负责 UI 线程编组。</summary>
    public void SetRulesPage(RulesPageViewModel viewModel) => RulesRoot.DataContext = viewModel;

    /// <summary>注入规则覆写卡片模型（规则页底部独立 DataContext）。调用方负责 UI 线程编组。</summary>
    public void SetRuleOverridePage(RuleOverrideViewModel viewModel) =>
        OverrideCardRoot.DataContext = viewModel;

    /// <summary>注入网络页系统代理区块模型。调用方负责 UI 线程编组。</summary>
    public void SetSystemProxySection(SystemProxySectionViewModel viewModel) =>
        SystemProxySectionRoot.DataContext = viewModel;

    /// <summary>注入代理页 TUN 快捷区模型。调用方负责 UI 线程编组。</summary>
    public void SetTunSection(TunSectionViewModel viewModel) =>
        TunSectionRoot.DataContext = viewModel;

    /// <summary>注入网络页 SSID 策略区模型。调用方负责 UI 线程编组。</summary>
    public void SetSsidStrategySection(SsidStrategySectionViewModel viewModel) =>
        SsidStrategyRoot.DataContext = viewModel;

    /// <summary>注入代理页 Provider 区模型。调用方负责 UI 线程编组。</summary>
    public void SetProxyProvidersSection(ProxyProvidersSectionViewModel viewModel) =>
        ProxyProvidersRoot.DataContext = viewModel;

    private void OnSsidBindCurrentClick(object sender, RoutedEventArgs e)
    {
        if (SsidStrategyRoot.DataContext is SsidStrategySectionViewModel viewModel)
        {
            viewModel.RequestBindCurrent();
        }
    }

    private void OnSsidUnbindClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SsidBindingRowViewModel row
            && SsidStrategyRoot.DataContext is SsidStrategySectionViewModel viewModel)
        {
            viewModel.RequestRemove(row.Ssid);
        }
    }

    /// <summary>注入设置页模型。调用方负责 UI 线程编组。</summary>
    public void SetSettingsPage(SettingsPageViewModel viewModel)
    {
        _settingsPage = viewModel;
        SettingsRoot.DataContext = viewModel;
    }

    private SettingsPageViewModel? _settingsPage;

    /// <summary>打开发布页（有更新 → release 页；否则 → releases 列表；源：footer 链接语义）。</summary>
    private void OnOpenReleasePageClick(object sender, RoutedEventArgs e) =>
        _settingsPage?.OpenReleasePage();

    public void SetConnectionsPage(ConnectionsPageViewModel viewModel)
    {
        ConnectionsRoot.DataContext = viewModel;
        ConnectionsList.MouseEnter += (_, _) => viewModel.IsReorderingPaused = true;
        ConnectionsList.MouseLeave += (_, _) => viewModel.IsReorderingPaused = false;
    }

    /// <summary>注入日志页模型。调用方负责 UI 线程编组。</summary>
    public void SetLogsPage(LogsPageViewModel viewModel)
    {
        _logsPage = viewModel;
        LogsRoot.DataContext = viewModel;
    }

    /// <summary>注入代理守护设置区模型。调用方负责 UI 线程编组。</summary>
    public void SetGuardSettings(ProxyGuardSettingsViewModel viewModel) =>
        GuardSettingsRoot.DataContext = viewModel;

    /// <summary>追加一行运行日志。调用方负责 UI 线程编组。</summary>
    public void AppendLog(string line)
    {
        if (_logsPage is null)
        {
            return;
        }

        _logsPage.AppendClashBarLog(line);
    }

    private void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        if (_logsPage is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_logsPage.CopyAllText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被占用（另一进程持有锁）时静默忽略
        }
    }

    private string? SelectedFileName =>
        (ConfigFilesBox.SelectedItem as ListBoxItem)?.Tag as string;

    private void OnCoreStartClick(object sender, RoutedEventArgs e) => CoreStartRequested?.Invoke();

    private void OnCoreStopClick(object sender, RoutedEventArgs e) => CoreStopRequested?.Invoke();

    private void OnCoreRestartClick(object sender, RoutedEventArgs e) => CoreRestartRequested?.Invoke();

    // ---- 远程机器切换器（源：headerConnectionControl 的胶囊对应物：状态点 + 下拉 + 管理） ----

    private bool _suppressMachineTargetEvents;

    private void OnMachineTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMachineTargetEvents)
        {
            return;
        }

        if (MachineTargetSelector.SelectedItem is ComboBoxItem item)
        {
            // Tag 为装箱 Guid（机器项）或 null（本机项）
            Guid? machineId = item.Tag is Guid id ? id : null;
            MachineTargetSwitchRequested?.Invoke(machineId);
        }
    }

    private void OnManageMachinesClick(object sender, RoutedEventArgs e) => ManageMachinesRequested?.Invoke();

    /// <summary>
    /// 程序化重建目标下拉（本机 + 机器项）并选中当前目标（不回环触发切换事件）。
    /// machineItems 为（id, 名称）对；activeMachineId 为 null 时选本机。
    /// </summary>
    public void SetMachineTargets(
        string localLabel,
        IReadOnlyList<(Guid Id, string Name)> machineItems,
        Guid? activeMachineId)
    {
        _suppressMachineTargetEvents = true;
        try
        {
            MachineTargetSelector.Items.Clear();
            var localItem = new ComboBoxItem { Content = localLabel, Tag = (Guid?)null };
            MachineTargetSelector.Items.Add(localItem);
            ComboBoxItem? activeItem = localItem;
            foreach ((Guid id, string name) in machineItems)
            {
                var item = new ComboBoxItem { Content = name, Tag = (Guid?)id };
                MachineTargetSelector.Items.Add(item);
                if (id == activeMachineId)
                {
                    activeItem = item;
                }
            }

            MachineTargetSelector.SelectedItem = activeItem;
        }
        finally
        {
            _suppressMachineTargetEvents = false;
        }
    }

    /// <summary>切换器状态点颜色与 tooltip（绿=已连接 / 红=失败 / 蓝=检测中 / 灰=未知或本机）。</summary>
    public void SetMachineStatusDot(string colorHex, string toolTip)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        MachineStatusDot.Fill = brush;
        MachineStatusDot.ToolTip = toolTip;
    }

    /// <summary>目标切换中（禁用下拉与管理入口；源：isSwitchingMachine）。</summary>
    public void SetMachineSwitching(bool switching)
    {
        MachineTargetSelector.IsEnabled = !switching;
        ManageMachinesButton.IsEnabled = !switching;
    }

    /// <summary>
    /// 远程目标的只读联动（源：isRemoteTarget 的各 guard——配置管理只读、
    /// 本机内核动作/守护/规则覆写禁用；订阅区显示「远程只读」提示）。
    /// </summary>
    public void SetRemoteReadonly(bool isRemote)
    {
        SubscriptionSectionRoot.IsEnabled = !isRemote;
        GuardSettingsRoot.IsEnabled = !isRemote;
        OverrideCardRoot.IsEnabled = !isRemote;
        SsidStrategyRoot.IsEnabled = !isRemote;   // 源：toggleCurrentSSIDBinding 的 remote guard
        RemoteReadonlyHint.Visibility = isRemote ? Visibility.Visible : Visibility.Collapsed;
        StartCoreButton.IsEnabled = !isRemote;
        StopCoreButton.IsEnabled = !isRemote;
        RestartCoreButton.IsEnabled = !isRemote;
    }

    private void OnRefreshSubscriptionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFileName is { } name)
        {
            SubscriptionRefreshRequested?.Invoke(name);
        }
    }

    private void OnRefreshAllSubscriptionsClick(object sender, RoutedEventArgs e) =>
        SubscriptionRefreshAllRequested?.Invoke();

    private void OnCopyProxyCommandClick(object sender, RoutedEventArgs e) =>
        CopyProxyCommandRequested?.Invoke();

    /// <summary>打开内核目录（源：维护区「打开内核目录」）。</summary>
    private void OnOpenCoreDirectoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = ClashBarXPaths.CoreDirectory();
            System.IO.Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (System.Exception)
        {
            // explorer 启动失败不影响主流程
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
