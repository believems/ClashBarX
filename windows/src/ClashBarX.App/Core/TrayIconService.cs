using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ClashBarX.App.Core;

/// <summary>托盘图标形态（与 macOS 菜单栏同源资产：BrandLogo / BrandRun / BrandSleep）。</summary>
public enum TrayIconKind
{
    Logo,
    Running,
    Sleeping,
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
    private TrayIconKind _kind;

    /// <summary>用户请求显示主窗（托盘单击或菜单）。</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>用户请求退出应用。</summary>
    public event Action? ExitRequested;

    /// <summary>用户切换系统代理开关（托盘菜单，参数为新状态）。</summary>
    public event Action<bool>? SystemProxyToggled;

    /// <summary>当前托盘图标形态。</summary>
    public TrayIconKind CurrentKind => _kind;

    private readonly MenuItem _proxyItem;
    private bool _suppressProxyItemEvents;

    public TrayIconService()
    {
        var showItem = new MenuItem { Header = "显示主窗(_S)" };
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();

        _proxyItem = new MenuItem { Header = "系统代理(_P)", IsCheckable = true };
        _proxyItem.Click += (_, _) =>
        {
            if (_suppressProxyItemEvents)
            {
                return;
            }

            SystemProxyToggled?.Invoke(_proxyItem.IsChecked);
        };

        var exitItem = new MenuItem { Header = "退出(_X)" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(_proxyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _logoIcon = LoadEmbeddedIcon("tray.ico");
        _runningIcon = LoadEmbeddedIcon("tray-run.ico");
        _sleepingIcon = LoadEmbeddedIcon("tray-sleep.ico");

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

    public void Dispose()
    {
        _taskbarIcon.Dispose();
        _logoIcon.Dispose();
        _runningIcon.Dispose();
        _sleepingIcon.Dispose();
    }

    /// <summary>从程序集内嵌资源（csproj Resource）加载 ICO。</summary>
    private static System.Drawing.Icon LoadEmbeddedIcon(string resourceName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{resourceName}");
        using Stream stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        return new System.Drawing.Icon(stream);
    }
}
