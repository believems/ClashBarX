using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ClashBarX.App.Core;

/// <summary>托盘图标形态（与 macOS 菜单栏同源资产：BrandLogo / BrandRun / BrandSleep / BrandTun）。</summary>
public enum TrayIconKind
{
    Logo,
    Running,
    Sleeping,

    /// <summary>运行中且 TUN 开启（源：StatusItemContentView 的 tunBrandStatusIconImage 分支）。</summary>
    Tun,
}

/// <summary>
/// 托盘常驻服务：品牌图标、提示文本、右键菜单（显示主窗 / 退出）与单击唤起主窗。
/// 图标直接以 System.Drawing.Icon 赋给基类 Icon 属性（同步注册，不走
/// IconSource 的异步转换链——该链在部分环境下静默失败，图标不出现在托盘）。
/// 无窗应用（实例不挂视觉树）必须显式 ForceCreate 才会注册托盘图标。
/// 退出仅发出事件，由 App 层执行内核终止与关停（保持职责分离）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly H.NotifyIcon.TaskbarIcon _taskbarIcon;
    private readonly System.Drawing.Icon _logoIcon;
    private readonly System.Drawing.Icon _runningIcon;
    private readonly System.Drawing.Icon _sleepingIcon;
    private readonly System.Drawing.Icon _tunIcon;
    private TrayIconKind _kind;

    /// <summary>用户请求显示主窗（托盘单击或菜单）。</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>用户请求退出应用。</summary>
    public event Action? ExitRequested;

    /// <summary>用户切换系统代理开关（托盘菜单，参数为新状态）。</summary>
    public event Action<bool>? SystemProxyToggled;

    /// <summary>用户切换分流模式（托盘菜单子项，参数为 rule/global/direct）。</summary>
    public event Action<string>? ModeChanged;

    /// <summary>用户请求复制代理命令（托盘二级菜单；参数 cmd / powershell / bash）。</summary>
    public event Action<string>? CopyProxyCommandRequested;

    /// <summary>手动内核生命周期（托盘内核操作组；与主窗互斥按钮同语义）。</summary>
    public event Action? CoreStartRequested;

    public event Action? CoreStopRequested;

    public event Action? CoreRestartRequested;

    /// <summary>用户请求关闭 TUN（托盘二级菜单「关闭 TUN」）。</summary>
    public event Action? TunOffRequested;

    /// <summary>用户选择 TUN stack（= 开启并切换；走与主窗同链路）。</summary>
    public event Action<string>? TunStackSelected;

    /// <summary>当前托盘图标形态。</summary>
    public TrayIconKind CurrentKind => _kind;

    private readonly MenuItem _proxyItem;
    private readonly MenuItem _modeRuleItem;
    private readonly MenuItem _modeGlobalItem;
    private readonly MenuItem _modeDirectItem;
    private readonly MenuItem _coreStartItem;
    private readonly MenuItem _coreStopItem;
    private readonly MenuItem _coreRestartItem;
    private readonly MenuItem _tunOffItem;
    private readonly MenuItem _tunSystemItem;
    private readonly MenuItem _tunGvisorItem;
    private readonly MenuItem _tunMixedItem;
    private readonly MenuItem _tunMipsItem;
    private bool _suppressProxyItemEvents;
    private bool _suppressModeItemEvents;
    private bool _suppressCoreItemEvents;
    private bool _suppressTunItemEvents;

    public TrayIconService()
    {
        var showItem = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.show_window") };
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();

        _proxyItem = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.system_proxy"), IsCheckable = true };
        _proxyItem.Click += (_, _) =>
        {
            if (_suppressProxyItemEvents)
            {
                return;
            }

            SystemProxyToggled?.Invoke(_proxyItem.IsChecked);
        };

        // TUN 二级菜单（定稿）：「关闭 TUN」+ 四种 stack 单选（选 stack=开启并切换）；
        // mips 不满足内核版本门槛时置灰并附提示（源：isMipsStackSupported）
        var tunMenu = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.tun") };
        _tunOffItem = MakeTunItem(ClashBarX.Core.L10n.CoreL10n.T("tray.menu.tun_off"), () => TunOffRequested?.Invoke());
        _tunSystemItem = MakeTunItem("system", () => TunStackSelected?.Invoke("system"));
        _tunGvisorItem = MakeTunItem("gvisor", () => TunStackSelected?.Invoke("gvisor"));
        _tunMixedItem = MakeTunItem("mixed", () => TunStackSelected?.Invoke("mixed"));
        _tunMipsItem = MakeTunItem(
            $"mips · {ClashBarX.Core.L10n.CoreL10n.T("tun.mips_requires_upgrade", ClashBarX.Core.Tun.TunControlService.MipsStackMinimumCoreVersion)}",
            () => TunStackSelected?.Invoke("mips"));
        tunMenu.Items.Add(_tunOffItem);
        tunMenu.Items.Add(new Separator());
        tunMenu.Items.Add(_tunSystemItem);
        tunMenu.Items.Add(_tunGvisorItem);
        tunMenu.Items.Add(_tunMixedItem);
        tunMenu.Items.Add(_tunMipsItem);

        // 分流模式子菜单（不打开主窗即可切换；源：菜单栏 mode 菜单语义）
        var modeMenu = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.mode") };
        _modeRuleItem = MakeModeItem(ClashBarX.Core.L10n.CoreL10n.T("tray.menu.mode_rule"), "rule");
        _modeGlobalItem = MakeModeItem(ClashBarX.Core.L10n.CoreL10n.T("tray.menu.mode_global"), "global");
        _modeDirectItem = MakeModeItem(ClashBarX.Core.L10n.CoreL10n.T("tray.menu.mode_direct"), "direct");
        modeMenu.Items.Add(_modeRuleItem);
        modeMenu.Items.Add(_modeGlobalItem);
        modeMenu.Items.Add(_modeDirectItem);

        // 复制代理命令（二级菜单：CMD / PowerShell / Bash——右键免开主窗即可取命令，与代理页三按钮同格式）。
        // 动作项非选择项：不加 IsCheckable（无 ✓ 槽），用 ⧉ 前缀图标与 TUN/分流的选择型二级区分
        var copyMenu = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.copy_proxy_command") };
        var cmdItem = new MenuItem { Header = "⧉ CMD" };
        cmdItem.Click += (_, _) => CopyProxyCommandRequested?.Invoke("cmd");
        var powerShellItem = new MenuItem { Header = "⧉ PowerShell" };
        powerShellItem.Click += (_, _) => CopyProxyCommandRequested?.Invoke("powershell");
        var bashItem = new MenuItem { Header = "⧉ Bash" };
        bashItem.Click += (_, _) => CopyProxyCommandRequested?.Invoke("bash");
        copyMenu.Items.Add(cmdItem);
        copyMenu.Items.Add(powerShellItem);
        copyMenu.Items.Add(bashItem);

        // 内核操作组（定稿：状态感知互斥——未运行=启动单项；运行中=停止+重启；远程目标整组置灰）
        _coreStartItem = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.core_start") };
        _coreStartItem.Click += (_, _) =>
        {
            if (!_suppressCoreItemEvents)
            {
                CoreStartRequested?.Invoke();
            }
        };
        _coreStopItem = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.core_stop") };
        _coreStopItem.Click += (_, _) =>
        {
            if (!_suppressCoreItemEvents)
            {
                CoreStopRequested?.Invoke();
            }
        };
        _coreRestartItem = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.core_restart") };
        _coreRestartItem.Click += (_, _) =>
        {
            if (!_suppressCoreItemEvents)
            {
                CoreRestartRequested?.Invoke();
            }
        };

        var exitItem = new MenuItem { Header = ClashBarX.Core.L10n.CoreL10n.T("tray.menu.exit") };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        // 紧凑菜单模板（去默认 Icon/check 大空白槽；Boss 真机反馈）
        menu.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new System.Uri("pack://application:,,,/Themes/TrayMenu.xaml", System.UriKind.Absolute),
        });
        menu.Items.Add(showItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_proxyItem);
        menu.Items.Add(tunMenu);
        menu.Items.Add(modeMenu);
        menu.Items.Add(copyMenu);
        menu.Items.Add(_coreStartItem);
        menu.Items.Add(_coreStopItem);
        menu.Items.Add(_coreRestartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _logoIcon = LoadEmbeddedIcon("tray.ico");
        _runningIcon = LoadEmbeddedIcon("tray-run.ico");
        _sleepingIcon = LoadEmbeddedIcon("tray-sleep.ico");
        _tunIcon = LoadEmbeddedIcon("tray-tun.ico");

        _taskbarIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = "ClashBarX",
            Icon = _logoIcon,
            ContextMenu = menu,
        };
        _taskbarIcon.TrayLeftMouseUp += (_, _) => ShowMainWindowRequested?.Invoke();
        _kind = TrayIconKind.Logo;

        // 无窗应用（StartupUri 移除、实例不挂视觉树）必须显式 ForceCreate 才会注册托盘图标
        //（源：H.NotifyIcon README 的 Windowless 用法）；不开节能模式——代理客户端需要正常调度
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>状态化提示文本（源：状态栏语义——tooltip 随内核状态与实时速率更新）。
    /// H.NotifyIcon 在托盘图标未就绪/已销毁的窗口期写 ToolTipText 会抛
    /// InvalidOperationException——吞掉等下次（真机实测启动早期偶发，非致命）。</summary>
    public void SetToolTip(string text)
    {
        _lastStatusToolTip = text;
        TrySetToolTipText(text);
    }

    private void TrySetToolTipText(string text)
    {
        try
        {
            _taskbarIcon.ToolTipText = text;
        }
        catch (InvalidOperationException)
        {
            // 图标窗口期：下次状态刷新自然重试
        }
    }

    /// <summary>
    /// 轻量即时反馈（源：macOS banner 的 Windows 对应物）：tooltip 闪现消息 3 秒后
    /// 恢复状态 tooltip（H.NotifyIcon 2.4.1 无气泡 API，tooltip 闪现为最接近的通道）。
    /// </summary>
    public void ShowTransientMessage(string message)
    {
        TrySetToolTipText($"ClashBarX · {message}");
        _tooltipResetTimer?.Dispose();
        _tooltipResetTimer = new System.Threading.Timer(
            _ => SetToolTip(_lastStatusToolTip),
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);
    }

    private System.Threading.Timer? _tooltipResetTimer;
    private string _lastStatusToolTip = "ClashBarX";

    /// <summary>切换托盘状态图标（内核运行中 / 已停止），与 macOS 菜单栏状态语义对齐。</summary>
    public void SetKind(TrayIconKind kind)
    {
        if (kind == _kind)
        {
            return;
        }

        _kind = kind;
        _taskbarIcon.Icon = kind switch
        {
            TrayIconKind.Running => _runningIcon,
            TrayIconKind.Sleeping => _sleepingIcon,
            TrayIconKind.Tun => _tunIcon,
            _ => _logoIcon,
        };
    }

    /// <summary>程序化同步系统代理菜单勾选态（不回环触发事件）。</summary>
    public void SetSystemProxyChecked(bool isChecked)
    {
        _suppressProxyItemEvents = true;
        try
        {
            _proxyItem.IsChecked = isChecked;
        }
        finally
        {
            _suppressProxyItemEvents = false;
        }
    }

    /// <summary>程序化同步分流模式勾选态（不回环触发事件）。</summary>
    public void SetModeChecked(string mode)
    {
        _suppressModeItemEvents = true;
        try
        {
            _modeRuleItem.IsChecked = mode == "rule";
            _modeGlobalItem.IsChecked = mode == "global";
            _modeDirectItem.IsChecked = mode == "direct";
        }
        finally
        {
            _suppressModeItemEvents = false;
        }
    }

    private MenuItem MakeModeItem(string header, string mode)
    {
        var item = new MenuItem { Header = header, IsCheckable = true };
        item.Click += (_, _) =>
        {
            if (_suppressModeItemEvents)
            {
                return;
            }

            ModeChanged?.Invoke(mode);
        };
        return item;
    }

    private MenuItem MakeTunItem(string header, Action activate)
    {
        var item = new MenuItem { Header = header, IsCheckable = true };
        item.Click += (_, _) =>
        {
            if (_suppressTunItemEvents)
            {
                return;
            }

            activate();
        };
        return item;
    }

    /// <summary>
    /// 内核操作组互斥（定稿：未运行=「启动内核」单项；运行中=「停止内核」+「重启内核」；
    /// 与主窗互斥按钮同语义）。
    /// </summary>
    public void SetCoreMenuState(bool coreRunning)
    {
        _coreStartItem.Visibility = coreRunning
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        _coreStopItem.Visibility = coreRunning
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        _coreRestartItem.Visibility = coreRunning
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    /// <summary>远程目标下内核操作组整组置灰（定稿：远程不可操控本机内核）。</summary>
    public void SetRemoteDisabled(bool isRemote)
    {
        _coreStartItem.IsEnabled = !isRemote;
        _coreStopItem.IsEnabled = !isRemote;
        _coreRestartItem.IsEnabled = !isRemote;
    }

    /// <summary>
    /// TUN 二级菜单同步（勾选态 + 当前 stack 单选 + mips 门槛置灰）。
    /// currentStack 为 null 时不选任何 stack 项。
    /// </summary>
    public void SetTunMenuState(bool tunEnabled, string? currentStack, bool mipsSupported)
    {
        _suppressTunItemEvents = true;
        try
        {
            _tunOffItem.IsChecked = !tunEnabled;
            _tunSystemItem.IsChecked = tunEnabled && currentStack == "system";
            _tunGvisorItem.IsChecked = tunEnabled && currentStack == "gvisor";
            _tunMixedItem.IsChecked = tunEnabled && currentStack == "mixed";
            _tunMipsItem.IsChecked = tunEnabled && currentStack == "mips";
            _tunMipsItem.IsEnabled = mipsSupported;
        }
        finally
        {
            _suppressTunItemEvents = false;
        }
    }

    public void Dispose()
    {
        _taskbarIcon.Dispose();
        _logoIcon.Dispose();
        _runningIcon.Dispose();
        _sleepingIcon.Dispose();
        _tunIcon.Dispose();
    }

    /// <summary>从程序集内嵌资源（csproj Resource）加载 ICO。</summary>
    private static System.Drawing.Icon LoadEmbeddedIcon(string resourceName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{resourceName}");
        using Stream stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        return new System.Drawing.Icon(stream);
    }
}
