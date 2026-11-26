using System.Windows;
using ClashBarX.App.Core;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Runtime;

namespace ClashBarX.App;

/// <summary>
/// 应用入口与生命周期：托盘常驻、mihomo sidecar 启停、API 就绪轮询与状态联动。
/// 启动序列：托盘就绪 → 探测内核与配置 → 存在则拉起 sidecar（no-core 时记录引导提示）
/// → 轮询 GET /version 驱动托盘状态图标（Running/Sleeping）与主窗状态行。
/// </summary>
public partial class App : Application
{
    /// <summary>MVP 默认控制器地址（后续接入「从配置读取 external-controller」后移除）。</summary>
    private const string DefaultController = "127.0.0.1:9090";

    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(12);

    private MihomoProcessManager? _processManager;
    private MihomoApiClient? _apiClient;
    private ApiStatusPoller? _poller;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _processManager = new MihomoProcessManager();
        _processManager.OnLog += line => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(line));
        _processManager.OnTermination += _ => Dispatcher.BeginInvoke(RefreshPresentationState);

        _mainWindow = new MainWindow();
        _tray = new TrayIconService();
        _tray.ShowMainWindowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitFromTray;

        // 启动即显示主窗（可见反馈）；关闭主窗 = 隐藏到托盘，退出走托盘菜单
        _mainWindow.Show();

        _ = StartCoreAtStartupAsync();
        StartApiPolling();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常退出路径也会先在此终结内核，避免任何路径漏杀
        _poller?.Dispose();
        _apiClient?.Dispose();
        _tray?.Dispose();
        _processManager?.StopImmediately();
        _processManager?.Dispose();
        base.OnExit(e);
    }

    private async Task StartCoreAtStartupAsync()
    {
        MihomoProcessManager manager = _processManager!;
        try
        {
            CoreLifecycleStatus status = await manager.StartAsync(
                ClashBarXPaths.DefaultConfigPath(),
                workingDirectory: null,
                DefaultController);
            _ = status;
        }
        catch (MihomoBinaryResolutionException)
        {
            _mainWindow?.AppendLog(
                $"[startup] 请将 mihomo.exe 放入 {ClashBarXPaths.CoreDirectory()}，"
                + $"并在 {ClashBarXPaths.DefaultConfigPath()} 准备运行配置后重启应用。");
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog($"[startup] {error.Message}");
        }
        finally
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
    }

    /// <summary>
    /// API 就绪轮询（源：AppViewModel+Polling.swift）：前台 4s / 后台 12s，
    /// healthy/degraded 状态与错误指纹去重日志由 Core 的 ApiStatusPoller 承担。
    /// </summary>
    private void StartApiPolling()
    {
        _apiClient = new MihomoApiClient(DefaultController);
        _poller = new ApiStatusPoller(
            _apiClient,
            intervalProvider: () => _mainWindow is { IsVisible: true } ? ForegroundPollInterval : BackgroundPollInterval);
        _poller.StatusChanged += _ => Dispatcher.BeginInvoke(RefreshPresentationState);
        _poller.VersionChanged += _ => Dispatcher.BeginInvoke(RefreshPresentationState);
        _poller.ErrorLogged += message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[api] {message}"));
        _poller.Start();
    }

    /// <summary>托盘状态图标与主窗状态行的统一刷新（源：macOS 菜单栏 run/sleep 状态语义）。</summary>
    private void RefreshPresentationState()
    {
        bool coreRunning = _processManager?.IsRunning == true;
        ApiHealth apiHealth = _poller?.Status ?? ApiHealth.Unknown;

        _tray?.SetKind(coreRunning
            ? apiHealth == ApiHealth.Healthy ? TrayIconKind.Running : TrayIconKind.Logo
            : TrayIconKind.Sleeping);

        if (_mainWindow == null)
        {
            return;
        }

        if (!coreRunning)
        {
            _mainWindow.SetCoreStatus("内核未运行");
        }
        else if (apiHealth == ApiHealth.Healthy)
        {
            string version = _poller?.Version is string v ? $" · {v}" : string.Empty;
            _mainWindow.SetCoreStatus($"内核运行中（pid={_processManager!.RunningProcessId}）· API 正常{version}");
        }
        else if (apiHealth == ApiHealth.Degraded)
        {
            _mainWindow.SetCoreStatus($"内核运行中（pid={_processManager!.RunningProcessId}）· API 未就绪");
        }
        else
        {
            _mainWindow.SetCoreStatus($"内核运行中（pid={_processManager!.RunningProcessId}）· 等待 API 响应");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitFromTray()
    {
        Shutdown();
    }
}
