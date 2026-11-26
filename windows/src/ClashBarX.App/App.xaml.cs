using System.Windows;
using ClashBarX.App.Core;

namespace ClashBarX.App;

/// <summary>
/// 应用入口与生命周期：托盘常驻、mihomo sidecar 启停、主窗显示控制。
/// M1 骨架的启动序列：托盘就绪 → 探测内核与配置 → 存在则拉起 sidecar（no-core 时记录引导提示）。
/// </summary>
public partial class App : Application
{
    /// <summary>MVP 默认控制器地址（M2 接入「从配置读取 external-controller」后移除）。</summary>
    private const string DefaultController = "127.0.0.1:9090";

    private MihomoProcessManager? _processManager;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _processManager = new MihomoProcessManager();
        _processManager.OnLog += line => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(line));
        _processManager.OnTermination += code =>
            Dispatcher.BeginInvoke(() =>
                _mainWindow?.SetCoreStatus($"内核已意外退出（exit={code}）"));

        _mainWindow = new MainWindow();
        _tray = new TrayIconService();
        _tray.ShowMainWindowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitFromTray;

        // 启动即显示主窗（可见反馈）；关闭主窗 = 隐藏到托盘，退出走托盘菜单
        _mainWindow.Show();

        _ = StartCoreAtStartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常退出路径也会先在此终结内核，避免任何路径漏杀
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
            _mainWindow?.SetCoreStatus(status == CoreLifecycleStatus.Running
                ? $"内核运行中（pid={manager.RunningProcessId}）"
                : status.ToString());
        }
        catch (MihomoBinaryResolutionException)
        {
            _mainWindow?.SetCoreStatus("未找到内核（no-core）");
            _mainWindow?.AppendLog(
                $"[startup] 请将 mihomo.exe 放入 {ClashBarXPaths.CoreDirectory()}，"
                + $"并在 {ClashBarXPaths.DefaultConfigPath()} 准备运行配置后重启应用。");
        }
        catch (Exception error)
        {
            _mainWindow?.SetCoreStatus("内核启动失败");
            _mainWindow?.AppendLog($"[startup] {error.Message}");
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
