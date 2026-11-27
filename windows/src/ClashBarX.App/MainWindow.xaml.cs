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

    /// <summary>请求导入本地配置文件（参数为源文件全路径；App 层拷贝进 configs 并启用）。</summary>
    public event Action<string>? LocalImportRequested;

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

    /// <summary>请求复制终端代理命令（参数 cmd / powershell / bash）。</summary>
    public event Action<string>? CopyProxyCommandRequested;

    /// <summary>用户请求切换机器目标（null = 本机；远程需 App 层验证连通性后生效）。</summary>
    public event Action<Guid?>? MachineTargetSwitchRequested;

    /// <summary>用户请求打开远程机器管理窗。</summary>
    public event Action? ManageMachinesRequested;

    /// <summary>用户请求打开控制器 WebUI（源：openControllerWebUI）。</summary>
    public event Action? OpenWebUIRequested;

    private void OnOpenWebUIClick(object sender, RoutedEventArgs e) => OpenWebUIRequested?.Invoke();

    public MainWindow()
    {
        InitializeComponent();
        MainTabs.SelectionChanged += (_, _) => TabSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>主 tab 选择变化（App 层在 UI 线程上缓存各页可见性，供后台流线程读取）。</summary>
    public event EventHandler? TabSelectionChanged;

    /// <summary>请求打开「关于」内容（应用菜单；App 层定位到设置页关于卡）。</summary>
    public event Action? AppAboutRequested;

    /// <summary>请求检查应用更新（应用菜单；App 层触发设置页检查命令）。</summary>
    public event Action? AppCheckUpdateRequested;

    public void SetCoreStatus(string status)
    {
        // 头部长文本已下沉底部状态栏；状态点只留颜色与悬停详情
        StatusStripText.Text = status;
        CoreStatusDot.ToolTip = status;
    }

    /// <summary>互斥内核按钮（未运行=启动单项；运行中=停止+重启；源：primaryCoreAction）。</summary>
    public void SetCoreRunning(bool running)
    {
        StartCoreButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopCoreButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RestartCoreButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>内核状态点颜色（绿=运行+API 正常 / 黄=API 未就绪 / 灰=未运行）。</summary>
    public void SetCoreStatusDot(string colorHex)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        CoreStatusDot.Fill = brush;
    }

    /// <summary>底部状态栏速率段（traffic WS 流）。</summary>
    public void SetStatusRate(string text) => StatusStripRate.Text = text;

    /// <summary>底部状态栏连接+内存段（connections/memory WS 流）。</summary>
    public void SetStatusMisc(string text) => StatusStripMisc.Text = text;

    private void OnAppMenuClick(object sender, RoutedEventArgs e)
    {
        // 图标点击在按钮下方弹出应用菜单（关闭窗口=收起到托盘）。
        // 必须延迟到当前鼠标消息处理完再开：Click 里同步 IsOpen=true 会被同一次
        // 点击的抬起消息当作「菜单外点击」当即关闭（真机实测菜单闪退不出现）
        ContextMenu menu = AppMenuButton.ContextMenu;
        menu.PlacementTarget = AppMenuButton;
        Dispatcher.BeginInvoke(
            new Action(() => menu.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnAppAboutClick(object sender, RoutedEventArgs e) => AppAboutRequested?.Invoke();

    private void OnAppCheckUpdateClick(object sender, RoutedEventArgs e) => AppCheckUpdateRequested?.Invoke();

    private void OnAppHideWindowClick(object sender, RoutedEventArgs e) => Hide();

    // ---- 自绘标题栏窗控（WindowStyle=None：拖动移窗/最小化/关闭；窗口固定尺寸，不提供最大化） ----

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        DragMove();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();   // OnClosing 取消并隐藏（托盘常驻）

    /// <summary>切换到设置页（应用菜单「关于/检查更新」的落点）。</summary>
    public void SelectSettingsTab() => MainTabs.SelectedIndex = 5;

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

    /// <summary>注入网络页代理端口卡模型（核心设置 VM 跨页共享；调用方负责 UI 线程编组）。</summary>
    public void SetProxyPortsSection(CoreSettingsViewModel viewModel) =>
        ProxyPortsRoot.DataContext = viewModel;

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

        // 维护/检查更新反馈走对话框（Boss 定稿：卡内不留状态行；消息以对话框形式展示）
        viewModel.MaintenanceFeedback += (message, isError) => Dispatcher.BeginInvoke(() =>
            MessageBox.Show(
                this,
                message,
                "ClashBarX",
                MessageBoxButton.OK,
                isError ? MessageBoxImage.Warning : MessageBoxImage.Information));
        viewModel.UpdateCheckCompleted += (message, hasUpdate) => Dispatcher.BeginInvoke(() =>
        {
            if (hasUpdate)
            {
                MessageBoxResult choice = MessageBox.Show(
                    this,
                    $"{message}\n{ClashBarX.Core.L10n.CoreL10n.T("update.dialog_open_release")}",
                    "ClashBarX",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (choice == MessageBoxResult.Yes)
                {
                    viewModel.OpenReleasePage();
                }
            }
            else
            {
                MessageBox.Show(this, message, "ClashBarX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
    }

    private SettingsPageViewModel? _settingsPage;


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

    private void OnCopyProxyCommandCmdClick(object sender, RoutedEventArgs e) =>
        CopyProxyCommandRequested?.Invoke("cmd");

    private void OnCopyProxyCommandPowerShellClick(object sender, RoutedEventArgs e) =>
        CopyProxyCommandRequested?.Invoke("powershell");

    private void OnCopyProxyCommandBashClick(object sender, RoutedEventArgs e) =>
        CopyProxyCommandRequested?.Invoke("bash");

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

    /// <summary>导入本地文件（源：订阅导入本地——选择 YAML 后拷贝进 configs 并启用）。</summary>
    private void OnImportLocalClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = ClashBarX.Core.L10n.CoreL10n.T("network.import_local.dialog_title"),
            Filter = "YAML (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            LocalImportRequested?.Invoke(dialog.FileName);
        }
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
