using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using ClashBarX.App.Core;
using ClashBarX.Core.Connections;
using ClashBarX.Core.Json;
using ClashBarX.Core.Logging;
using ClashBarX.Core.Maintenance;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Overrides;
using ClashBarX.Core.Proxies;
using ClashBarX.Core.ProxyGuard;
using ClashBarX.Core.Remote;
using ClashBarX.Core.Ssid;
using ClashBarX.Core.Rules;
using ClashBarX.Core.Runtime;
using ClashBarX.Core.Streams;
using ClashBarX.Core.Subscriptions;
using ClashBarX.Core.SystemProxy;
using ClashBarX.Core.Tun;
using ClashBarX.Core.Updates;
using ClashBarX.Core.WebUI;

namespace ClashBarX.App;

/// <summary>
/// 应用入口与生命周期：托盘常驻、mihomo sidecar 启停、API 就绪轮询、系统代理开关与订阅管理。
/// 启动序列：托盘就绪 → 探测内核与配置（优先订阅选中配置）→ 拉起 sidecar → 轮询 /version
/// → 按持久化设置恢复系统代理开启意图。
/// </summary>
public partial class App : Application
{
    /// <summary>MVP 默认控制器地址（后续接入「从配置读取 external-controller」后移除）。</summary>
    private const string DefaultController = "127.0.0.1:9090";

    /// <summary>订阅下载 UA 的缺省内核版本（源：defaultMihomoVersionForUserAgent）。</summary>
    private const string DefaultMihomoVersionForUserAgent = "v1.19.27";

    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(12);

    private readonly AppSettingsStore _settingsStore = new();
    private SubscriptionService? _subscriptions;

    private MihomoProcessManager? _processManager;
    private MihomoApiClient? _apiClient;
    private ApiStatusPoller? _poller;
    private SystemProxyService? _systemProxy;
    private SystemProxySectionViewModel? _systemProxySection;
    private RegistryWatcher? _proxyWatcher;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private ProxyPageViewModel? _proxyPage;
    private ProxyBoardService? _proxyBoard;
    private ProxyProvidersSectionViewModel? _proxyProviders;
    private TunControlService? _tunControl;
    private TunSectionViewModel? _tunSection;
    private RemoteMachineStore? _remoteStore;
    /// <summary>机器目标切换中（源：isSwitchingMachine——禁用头部切换器与 WebUI 入口）。</summary>
    private bool _isSwitchingMachine;
    private WlanSsidMonitor? _ssidMonitor;
    private SsidStrategySectionViewModel? _ssidStrategy;
    /// <summary>SSID 策略日志指纹去重（源：ssidStrategyLastLogFingerprint）。</summary>
    private string? _ssidStrategyLastLogFingerprint;
    private AppReleaseService? _releaseService;
    private AppReleaseService? _mihomoReleaseService;
    /// <summary>WebUI 入口地址（源：controllerUIURL——随配置快照与切换目标刷新）。</summary>
    private string _controllerUiUrl = ControllerWebUiUrl.Build(
        DefaultController, null, externalUiConfigured: false, externalUiName: null);
    private ConnectionsPageViewModel? _connectionsPage;
    private ConnectionStatisticsService? _connectionStatistics;
    private bool _connectionsProcessMatchingEnabled;
    private RulesBoardService? _rulesBoard;
    private RulesPageViewModel? _rulesPage;
    private RuleOverrideService? _ruleOverride;
    private RuleOverrideViewModel? _ruleOverridePage;
    private SettingsPageViewModel? _settingsPage;
    private bool _coreSettingsLoaded;
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _configRootWatcher;
    private readonly object _configDebounceGate = new();
    private System.Threading.Timer? _configDebounceTimer;
    private string? _pendingConfigChangePath;

    /// <summary>连接页 UI 可见性快照（UI 线程维护，供 WS 后台线程读取——直接读 WPF 属性会跨线程异常）。</summary>
    private volatile bool _connectionsUiVisible;

    /// <summary>连接页统计视图态快照（UI 线程维护：VM 属性变化经 PropertyChanged 联动刷新）。</summary>
    private volatile bool _connectionsShowsStatistics;
    private TrafficStatisticsDimension _connectionsStatisticsDimension = TrafficStatisticsDimension.Node;
    private LogsPageViewModel? _logsPage;
    private DispatcherTimer? _mihomoLogFlushTimer;
    private ProxyGuardService? _proxyGuard;
    private ProxyGuardSettingsViewModel? _guardSettings;
    /// <summary>守护周期秒数（UI 线程写、守护后台循环每轮读——volatile 保证可见性）。</summary>
    private volatile int _guardIntervalSeconds = 300;
    private bool _cliProxyGuardTrigger;
    private EventWaitHandle? _guardTriggerEvent;
    private StreamCoordinator? _streams;
    private System.Threading.Timer? _proxyRefreshTimer;
    private string? _lastProxyRefreshError;
    private System.Threading.Timer? _rulesRefreshTimer;
    private string? _lastRulesRefreshError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未处理异常兜底：写文件留栈（否则 WinExe 崩溃无任何现场，真机排障无从下手）
        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(
                Path.Combine(ClashBarXPaths.AppDataRoot(), "crash-ui.log"),
                $"[UI {DateTime.Now:O}] {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            File.WriteAllText(
                Path.Combine(ClashBarXPaths.AppDataRoot(), "crash.log"),
                $"[{DateTime.Now:O}] {args.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            File.WriteAllText(
                Path.Combine(ClashBarXPaths.AppDataRoot(), "crash-task.log"),
                $"[task {DateTime.Now:O}] {args.Exception}");
            args.SetObserved();
        };

        // CLI 触发（源：--proxy-guard——已有实例时通知其立即探测后本进程退出
        //（ProxyGuardCLI 的 exit(0) 语义）；本实例为首个实例时随启动触发）
        _cliProxyGuardTrigger = Environment.GetCommandLineArgs()
            .Contains(ProxyGuardService.CliTriggerArgument, StringComparer.OrdinalIgnoreCase);
        if (_cliProxyGuardTrigger && TryTriggerRunningGuardInstance())
        {
            Shutdown();   // 已通知运行中实例，不再启动第二份主窗/托盘/内核（避免抢占 9090 控制端口）
            return;
        }

        // 卸载器温和关闭（源：UninstallRun 的 --exit——通知运行实例退出后自身退出）
        if (Environment.GetCommandLineArgs().Contains(ExitArgument, StringComparer.OrdinalIgnoreCase))
        {
            TryNotifyExitToRunningInstance();
            Shutdown();
            return;
        }

        _processManager = new MihomoProcessManager();
        _processManager.OnLog += line => Dispatcher.BeginInvoke(() =>
            _logsPage?.QueueMihomoLog(MihomoLogDecoder.NormalizeLevel(line), line));
        _processManager.OnTermination += _ => Dispatcher.BeginInvoke(RefreshPresentationState);

        _subscriptions = new SubscriptionService(
            Path.Combine(ClashBarXPaths.AppDataRoot(), "configs"),
            Path.Combine(ClashBarXPaths.AppDataRoot(), "subscriptions.json"));

        _mainWindow = new MainWindow();
        _mainWindow.SubscriptionImportRequested += OnSubscriptionImportRequested;
        _mainWindow.LocalImportRequested += OnLocalImportRequested;
        _mainWindow.ConfigActivateRequested += OnConfigActivateRequested;
        _mainWindow.ConfigRemoveRequested += OnConfigRemoveRequested;
        _mainWindow.CoreStartRequested += () => _ = StartCoreManualAsync();
        _mainWindow.CoreStopRequested += () => _ = StopCoreManualAsync();
        _mainWindow.CoreRestartRequested += () => _ = RestartCoreManualAsync();
        _mainWindow.SubscriptionRefreshRequested += OnSubscriptionRefreshRequested;
        _mainWindow.SubscriptionRefreshAllRequested += OnSubscriptionRefreshAllRequested;
        _mainWindow.CopyProxyCommandRequested += shell => _ = OnCopyProxyCommandRequestedAsync(shell);
        _mainWindow.MachineTargetSwitchRequested += machineId => _ = SwitchMachineTargetAsync(machineId);
        _mainWindow.ManageMachinesRequested += OpenRemoteMachineManager;
        _mainWindow.OpenWebUIRequested += OpenControllerWebUI;
        // 应用菜单（左上角 App 图标）：关于=定位设置页关于卡；检查更新=设置页检查命令
        _mainWindow.AppAboutRequested += () => Dispatcher.BeginInvoke(() => _mainWindow.SelectSettingsTab());
        _mainWindow.AppCheckUpdateRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _mainWindow.SelectSettingsTab();
            _settingsPage?.CheckUpdateCommand.Execute(null);
        });

        _tray = new TrayIconService();
        _tray.ShowMainWindowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitFromTray;
        _tray.SystemProxyToggled += OnSystemProxyToggled;
        _tray.CopyProxyCommandRequested += shell => _ = OnCopyProxyCommandRequestedAsync(shell);
        // 托盘内核操作组（定稿：与主窗互斥按钮同链路）
        _tray.CoreStartRequested += () => _ = StartCoreManualAsync();
        _tray.CoreStopRequested += () => _ = StopCoreManualAsync();
        _tray.CoreRestartRequested += () => _ = RestartCoreManualAsync();
        // 托盘 TUN 二级菜单（定稿：关 TUN / 选 stack=开启并切换，走与主窗同链路含 UAC 引导）
        _tray.TunOffRequested += () => Dispatcher.BeginInvoke(() => _ = OnTunToggleRequestedAsync(false));
        _tray.TunStackSelected += stack => Dispatcher.BeginInvoke(() => _ = OnTunStackSelectedAsync(stack));
        // 托盘分流模式子菜单：经代理页 VM 走统一 PATCH 链路（UI radio 同步）
        _tray.ModeChanged += mode => Dispatcher.BeginInvoke(() =>
        {
            if (_proxyPage != null)
            {
                _proxyPage.Mode = mode;
            }
        });

        // 启动即显示主窗（可见反馈）；关闭主窗 = 隐藏到托盘，退出走托盘菜单
        _mainWindow.Show();
        RefreshConfigList();

        // 远程机器仓库（源：RemoteMachineStore——启动恢复激活目标，远程目标下
        // 不拉本机内核（源：Initialization 的 isRemoteTarget guard）且 API 指向远程）
        _remoteStore = new RemoteMachineStore();
        _remoteStore.MachinesChanged += () => Dispatcher.BeginInvoke(() =>
        {
            RefreshMachineSelector();
            ApplyRemoteReadonlyState();
        });
        _remoteStore.StatusChanged += machineId => Dispatcher.BeginInvoke(() => UpdateMachineStatusDot(machineId));
        RefreshMachineSelector();
        ApplyRemoteReadonlyState();

        _ = StartCoreAtStartupAsync();
        StartApiPolling();
        SetupSystemProxy();
        SetupProxyPage();
        SetupTunSection();
        SetupSsidStrategy();
        SetupRulesPage();
        SetupConnectionsPage();
        SetupLogsPage();
        SetupProxyGuard();
        SetupSettingsPage();
        SetupConfigMonitoring();
    }

    // ---- 设置页（M4-3：外观/语言/自启/流量概览；源 AppViewModel+Settings.swift） ----

    private void SetupSettingsPage()
    {
        AppSettings saved = _settingsStore.Load();
        var appearance = saved.AppearanceMode switch
        {
            "light" => AppAppearanceMode.Light,
            "dark" => AppAppearanceMode.Dark,
            _ => AppAppearanceMode.System,
        };
        var language = saved.Language == "en" ? AppLanguage.English : AppLanguage.ChineseSimplified;

        _settingsPage = new SettingsPageViewModel
        {
            LaunchAtLoginRequested = enabled =>
            {
                new LaunchAtLoginService().Apply(enabled, Environment.ProcessPath!);
                SaveCurrentSettings();
                return Task.CompletedTask;
            },
        };
        _settingsPage.CoreSettings.PatchConfigAsync = async (key, value) =>
        {
            try
            {
                await _apiClient!.RequestNoResponseAsync(
                    MihomoEndpoint.PatchConfigs(new JsonObject { [key] = JsonValue.Create(value) }));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _mainWindow?.AppendLog(L("log.settings.patch_failed", key, error.Message));
            }
        };
        _settingsPage.CoreSettings.RestoreCloseConnections(saved.CloseConnectionsOnSwitch);
        _settingsPage.CoreSettings.CloseConnectionsChanged += _ => SaveCurrentSettings();
        _settingsPage.CoreSettings.LogLevelChanged += level => _logsPage?.SyncLogLevel(level);

        // 维护动作（源：AppViewModel+Maintenance——GEO 热更新与内核自升级）
        var maintenance = new MaintenanceService(_apiClient!);
        _settingsPage.UpgradeGeoRequested = async () =>
        {
            CoreUpgradeResponse response = await maintenance.UpgradeGeoAsync();
            string message = string.IsNullOrWhiteSpace(response.Message)
                ? L("log.maintenance.geo_no_message", response.Status ?? L("log.maintenance.submitted"))
                : L("log.maintenance.geo", response.Status ?? L("log.maintenance.submitted"), response.Message);
            _mainWindow?.AppendLog($"[maintenance] {message}");
            return message;
        };
        _settingsPage.UpgradeCoreRequested = async () =>
        {
            CoreUpgradeResponse response = await maintenance.UpgradeCoreAsync();
            string message = string.IsNullOrWhiteSpace(response.Message)
                ? L("log.maintenance.core_no_message", response.Status ?? L("log.maintenance.submitted"))
                : L("log.maintenance.core", response.Status ?? L("log.maintenance.submitted"), response.Message);
            _mainWindow?.AppendLog($"[maintenance] {message}");
            return message;
        };
        _settingsPage.FlushFakeIPRequested = async () =>
        {
            await maintenance.FlushFakeIPCacheAsync();
            string message = L("log.maintenance.flush_fakeip");
            _mainWindow?.AppendLog($"[maintenance] {message}");
            return message;
        };
        _settingsPage.FlushDNSRequested = async () =>
        {
            await maintenance.FlushDNSCacheAsync();
            string message = L("log.maintenance.flush_dns");
            _mainWindow?.AppendLog($"[maintenance] {message}");
            return message;
        };
        _settingsPage.AppearanceChanged += mode =>
        {
            AppearanceManager.ApplyTheme(mode);
            SaveCurrentSettings();
        };
        _settingsPage.LanguageChanged += languageChanged =>
        {
            AppearanceManager.ApplyLanguage(languageChanged);
            SaveCurrentSettings();
            RefreshPresentationState();   // 托盘 tooltip 与状态行随语言即时更新（评审 P2）
        };

        // 关于与更新检查（源：refreshLatestAppRelease + availableAppUpdate + footer 链接）。
        // 应用自身走发版列表按 Windows 资产过滤：/releases/latest 不区分平台，macOS 发版较新时会误报
        _releaseService = new AppReleaseService();
        string currentVersion = CurrentAppVersionText();
        _settingsPage.CurrentVersionText = currentVersion;
        _settingsPage.CheckUpdateRequested = async () =>
            AppReleaseService.ResolveAvailableUpdate(
                await _releaseService!.FetchLatestWindowsReleaseAsync(currentVersion), currentVersion);
        // 内核块更新检查（定稿：mihomo releases latest 对比当前内核版本；发现新版后才显示升级按钮）
        _mihomoReleaseService = new AppReleaseService(
            latestReleaseUrl: "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest");
        _settingsPage.CheckKernelUpdateRequested = async () =>
        {
            string kernelVersion = _lastKnownCoreVersion is { Length: > 0 } known ? known : "0.0.0";
            return AppReleaseService.ResolveAvailableUpdate(
                await _mihomoReleaseService.FetchLatestReleaseAsync(kernelVersion), kernelVersion);
        };
        _settingsPage.OpenReleasePageRequested += url =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                _mainWindow?.AppendLog(L("log.about.open_failed", error.Message));
            }
        };
        _ = CheckUpdateAtStartupAsync();
        _settingsPage.TrafficOverviewChanged += visible =>
        {
            if (_proxyPage != null)
            {
                _proxyPage.ShowTrafficOverview = visible;
            }

            SaveCurrentSettings();
        };
        _settingsPage.Restore(appearance, language, saved.LaunchAtLogin, saved.ShowTrafficOverview);
        _proxyPage!.ShowTrafficOverview = saved.ShowTrafficOverview;
        AppearanceManager.ApplyTheme(appearance);   // 启动应用持久化外观
        AppearanceManager.ApplyLanguage(language);   // 启动应用持久化语言
        _mainWindow!.SetSettingsPage(_settingsPage);
        _mainWindow.SetProxyPortsSection(_settingsPage.CoreSettings);   // 网络页端口卡跨页共享核心设置 VM
    }

    /// <summary>切换内核日志等级（日志页快捷入口；PATCH /configs log-level）。</summary>
    private async Task PatchLogLevelAsync(string level)
    {
        try
        {
            await _apiClient!.RequestNoResponseAsync(
                MihomoEndpoint.PatchConfigs(new JsonObject { ["log-level"] = level }));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.logs.level_failed", error.Message));
        }
    }

    /// <summary>切换分流模式（rule/global/direct；PATCH /configs 即时生效）。</summary>
    private async Task PatchModeAsync(string mode)
    {
        try
        {
            await _apiClient!.RequestNoResponseAsync(
                MihomoEndpoint.PatchConfigs(new JsonObject { ["mode"] = mode }));
            _mainWindow?.AppendLog(L("log.mode.switched", mode));
            _tray?.ShowTransientMessage(L("log.tray.mode_changed", mode));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.mode.switch_failed", error.Message));
        }
    }

    /// <summary>API 就绪后拉一次内核运行配置（核心设置区初始化；不触发 PATCH）。</summary>
    private async Task LoadCoreSettingsSnapshotAsync()
    {
        if (_coreSettingsLoaded)
        {
            return;
        }

        try
        {
            ConfigSnapshot snapshot = await _apiClient!.RequestAsync<ConfigSnapshot>(MihomoEndpoint.GetConfigs());
            _coreSettingsLoaded = true;
            await Dispatcher.BeginInvoke(() =>
            {
                _settingsPage?.CoreSettings.ApplySnapshot(
                    snapshot.AllowLan, snapshot.Ipv6, snapshot.TcpConcurrent, snapshot.LogLevel,
                    snapshot.MixedPort, snapshot.Port, snapshot.SocksPort, snapshot.RedirPort, snapshot.TproxyPort);
                _proxyPage?.ApplyMode(snapshot.Mode ?? "rule");
                _tray?.SetModeChecked(snapshot.Mode ?? "rule");
                _logsPage?.SyncLogLevel(snapshot.LogLevel ?? "info");
                _tunSection?.ApplySnapshot(snapshot.TunEnabled ?? false, snapshot.Tun?.Stack);   // 运行态显示同步（源：AppViewModel+Polling 的 tunStack 同步）
                _ = RestoreTunAtStartupAsync();   // API 就绪后的 TUN 恢复链路（源：validate/verify/align 三连）
                ApplyControllerUiUrl(snapshot);   // WebUI 入口随配置刷新（源：applyExternalUIConfiguration + controllerUIURL）
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // API 未就绪/瞬断：静默等待下次 healthy 事件重试
            _ = error;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常退出路径也会先在此终结内核，避免任何路径漏杀
        _proxyRefreshTimer?.Dispose();
        _rulesRefreshTimer?.Dispose();
        _ssidMonitor?.Dispose();
        _releaseService?.Dispose();
        _mihomoReleaseService?.Dispose();
        _configWatcher?.Dispose();
        _configRootWatcher?.Dispose();
        _configDebounceTimer?.Dispose();
        _mihomoLogFlushTimer?.Stop();
        _proxyGuard?.Dispose();
        _guardTriggerEvent?.Dispose();
        _streams?.Dispose();
        _proxyWatcher?.Dispose();
        _poller?.Dispose();
        _apiClient?.Dispose();

        // 内核随退出终结，系统代理必须一并关闭——否则注册表指向死端口、用户断网
        if (_systemProxy?.ExpectedTarget != null)
        {
            _systemProxy.Disable();
        }

        _systemProxy?.Dispose();
        _tray?.Dispose();
        _processManager?.StopImmediately();
        _processManager?.Dispose();
        base.OnExit(e);
    }

    private async Task StartCoreAtStartupAsync()
    {
        // 远程目标不拉本机内核（源：AppViewModel+Initialization 的 isRemoteTarget guard）
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;
        }

        // 没有可启动的配置（还没导入订阅）：不把不存在的配置路径交给内核（内核会立刻退出）；
        // 启动自动拉起不弹窗（源：auto trigger 的 startupErrorMessage 语义），由代理页与订阅区的空态提示引导
        if (!HasRunnableConfig())
        {
            _mainWindow?.AppendLog(L("log.start.no_config_hint"));
            return;
        }

        MihomoProcessManager manager = _processManager!;
        try
        {
            // 优先订阅选中配置；否则根目录手工 config.yaml；覆写启用时换 runtime 配置
            string configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
            (string launchPath, string? workingDirectory) = ResolveCoreLaunchConfig(configPath);
            CoreLifecycleStatus status = await manager.StartAsync(
                launchPath,
                workingDirectory,
                DefaultController);
            _ = status;
        }
        catch (MihomoBinaryResolutionException)
        {
_mainWindow?.AppendLog(L("log.core.startup_hint", ClashBarXPaths.CoreDirectory(), ClashBarXPaths.DefaultConfigPath()));
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
        // 远程目标时 API 指向远程控制器（源：switchToMachineTarget 的 controller 切换）；
        // UpdateCredentials 后流与轮询随连接工厂闭包自动跟随新目标
        (string controller, string? secret) = ResolveActiveController();
        _apiClient = new MihomoApiClient(controller, secret);
        _poller = new ApiStatusPoller(
            _apiClient,
            intervalProvider: () => _mainWindow is { IsVisible: true } ? ForegroundPollInterval : BackgroundPollInterval);
        _poller.StatusChanged += status =>
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
            Dispatcher.BeginInvoke(() => UpdateMachineStatusDot(null));   // 本机目标时头部点跟随 API 健康
            if (status == ApiHealth.Healthy && !_coreSettingsLoaded)
            {
                Dispatcher.BeginInvoke(() => _ = LoadCoreSettingsSnapshotAsync());
            }
        };
        _poller.VersionChanged += _ => Dispatcher.BeginInvoke(() =>
        {
            RefreshPresentationState();
            RefreshTunMipsSupport();   // 内核版本就绪后刷新 mips 门槛（源：isMipsStackSupported）
        });
        _poller.ErrorLogged += message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[api] {message}"));
        _poller.Start();
    }

    // ---- 系统代理（源：AppViewModel+SystemProxy.swift 的意图/守护语义） ----

    private void SetupSystemProxy()
    {
        var controller = new WindowsSystemProxyController();
        _systemProxy = new SystemProxyService(controller);
        _systemProxy.Logged += message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message));
        _systemProxy.EnabledChanged += enabled => Dispatcher.BeginInvoke(() =>
        {
            _tray?.SetSystemProxyChecked(enabled);
            _systemProxySection?.SetEnabled(enabled);   // 网络页开关镜像
        });

        _proxyWatcher = new RegistryWatcher(
            WindowsSystemProxyController.InternetSettingsRegistryPath,
            () => Dispatcher.BeginInvoke(() => _systemProxy?.NotifyRegistryChanged()))
        {
            Logged = message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message)),
        };
        _proxyWatcher.Start();

        // 网络页系统代理区块（开关 + 绕过列表）
        SetupNetworkPage();

        // 初始观测同步（外部工具可能已开/关代理）
        SystemProxySnapshot snapshot = _systemProxy.RefreshState();
        _tray?.SetSystemProxyChecked(snapshot.Enabled);
        _systemProxySection?.SetEnabled(snapshot.Enabled);

        // 启动恢复开启意图（源：startup repair 语义）：等待 API 就绪后拉取混合端口
        if (_settingsStore.Load().SystemProxyEnabled)
        {
            _ = EnableSystemProxyFromCoreConfigAsync(reason: "startup restore");
        }
    }

    /// <summary>网络页系统代理区块：开关上抛 + 绕过列表持久化与重写（源：saveSystemProxyExceptions）。</summary>
    private void SetupNetworkPage()
    {
        _systemProxySection = new SystemProxySectionViewModel(SaveSystemProxyBypassListAsync);
        _systemProxySection.LoadExceptions(_settingsStore.Load().SystemProxyBypassList);
        _systemProxySection.ToggleRequested += enable =>
        {
            if (enable)
            {
                _ = EnableSystemProxyFromCoreConfigAsync(reason: "user toggle");
            }
            else
            {
                _systemProxy?.Disable();
                _settingsStore.Save(_settingsStore.Load() with { SystemProxyEnabled = false });
            }
        };
        _mainWindow!.SetSystemProxySection(_systemProxySection);
    }

    /// <summary>保存绕过列表：持久化到设置；系统代理开着时用新列表重写（源：setExceptionsList 语义）。</summary>
    private async Task SaveSystemProxyBypassListAsync()
    {
        IReadOnlyList<string> bypass = _systemProxySection?.NormalizedExceptions()
            ?? SystemProxySectionViewModel.DefaultBypassList.ToList();

        AppSettings saved = _settingsStore.Load();
        _settingsStore.Save(saved with { SystemProxyBypassList = bypass });

        if (_systemProxy?.ExpectedTarget is { } expected)
        {
            try
            {
                _systemProxy.Enable(new SystemProxyTarget(expected.Host, expected.Port, bypass));
                _mainWindow?.AppendLog(L("log.system_proxy.bypass_saved_rewrite", bypass.Count));
            }
            catch (Exception error)
            {
                _mainWindow?.AppendLog(L("log.system_proxy.bypass_rewrite_failed", error.Message));
            }
        }
        else
        {
            _mainWindow?.AppendLog(L("log.system_proxy.bypass_saved", bypass.Count));
        }

        await Task.CompletedTask;
    }

    private void OnSystemProxyToggled(bool enable)
    {
        if (enable)
        {
            _ = EnableSystemProxyFromCoreConfigAsync(reason: "user toggle");
        }
        else
        {
            _systemProxy?.Disable();
            _settingsStore.Save(_settingsStore.Load() with { SystemProxyEnabled = false });
        }
    }

    /// <summary>开启系统代理：从 GET /configs 取混合端口后写入期望目标。</summary>
    private async Task EnableSystemProxyFromCoreConfigAsync(string reason)
    {
        try
        {
            ConfigSnapshot config = await _apiClient!.RequestAsync<ConfigSnapshot>(MihomoEndpoint.GetConfigs());
            int mixedPort = config.MixedPort ?? 0;
            if (mixedPort <= 0)
            {
                _mainWindow?.AppendLog(L("log.system_proxy.no_mixed_port", reason));
                Dispatcher.BeginInvoke(() =>
                {
                    _tray?.SetSystemProxyChecked(false);
                });
                _settingsStore.Save(_settingsStore.Load() with { SystemProxyEnabled = false });
                return;
            }

            // 绕过列表优先用持久化值（源：saveSystemProxyExceptions 后的启用链路）
            IReadOnlyList<string> bypass =
                _settingsStore.Load().SystemProxyBypassList ?? SystemProxyDefaults.DefaultBypassList;
            // 远程目标时系统代理指向远程内核（源：resolveSystemProxyTargetFromRuntimeConfig 的 controllerHost）
            string proxyHost = _remoteStore?.ActiveMachine is { } machine ? machine.Host : "127.0.0.1";
            _systemProxy?.Enable(new SystemProxyTarget(proxyHost, mixedPort, bypass));
            _settingsStore.Save(_settingsStore.Load() with { SystemProxyEnabled = true });
            _tray?.ShowTransientMessage(L("log.system_proxy.enabled", proxyHost, mixedPort));
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog($"[system proxy] {reason}: {error.Message}");
        }
    }

    // ---- 代理页（M2-4：信息架构源 ProxyTabView 的分组卡） ----

    private void SetupProxyPage()
    {
        _proxyBoard = new ProxyBoardService(_apiClient!);
        _proxyPage = new ProxyPageViewModel(
            _proxyBoard,
            message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message)),
            () => _processManager?.IsRunning == true);
        _proxyPage.ModeChanged += mode =>
        {
            _tray?.SetModeChecked(mode);   // 托盘子菜单勾选同步（抑制回环）
            _ = PatchModeAsync(mode);
        };
        _mainWindow!.DataContext = _proxyPage;

        // Provider 区（M5-5：源 proxyProvidersSection——更新全部/单刷/healthcheck）
        _proxyProviders = new ProxyProvidersSectionViewModel();
        _proxyProviders.UpdateAllRequested += () => _ = UpdateAllProvidersAsync();
        _proxyProviders.UpdateProviderRequested += name => _ = UpdateProviderAsync(name);
        _proxyProviders.HealthcheckProviderRequested += name => _ = HealthcheckProviderAsync(name);
        _mainWindow.SetProxyProvidersSection(_proxyProviders);

        // 一键测全部分组（M5-6：源 refreshAllGroupLatencies——hidden 视开关、并发组测速）
        _proxyPage.TestAllGroupsRequested += () => _ = TestAllGroupsAsync();

        // traffic WS 流（M1 的 StreamCoordinator：重连去抖复用）
        _streams = new StreamCoordinator
        {
            ShouldReconnect = () => _processManager?.IsRunning == true,
            OnDisconnect = (key, message) =>
                Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(L("log.stream.disconnected", key, message))),
        };
        StartTrafficStream();

        // 代理 tab 前台时的分组刷新（源：medium 频率 4s 随 tab 激活）
        _proxyRefreshTimer = new System.Threading.Timer(
            _ => Dispatcher.BeginInvoke(() => _ = RefreshProxyBoardAsync()),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4));
    }

    private void StartTrafficStream()
    {
        _streams!.Start(
            "traffic",
            () => _apiClient!.CreateWebSocketConnection(MihomoEndpoint.Traffic()),
            async payload =>
            {
                try
                {
                    TrafficSnapshot? snapshot = JsonSerializer.Deserialize<TrafficSnapshot>(
                        payload.Span, JsonSharedOptions.Json);
                    if (snapshot != null)
                    {
                        await Dispatcher.BeginInvoke(() =>
                        {
                            _proxyPage?.UpdateTraffic(
                                snapshot.Up, snapshot.Down, snapshot.UpTotal, snapshot.DownTotal);
                            _trayTraffic = _proxyPage?.TrafficSummary ?? string.Empty;
                            UpdateTrayToolTip();
                            _mainWindow?.SetStatusRate(_proxyPage?.StatusRateText ?? "—");
                        });
                    }
                }
                catch (JsonException)
                {
                    // 噪声/半包 payload 丢弃（normalize 已滤常见噪声）
                }
                catch (Exception error)
                {
                    // 解码侧意外异常必须编组到 UI 线程记录，绝不能在线程池直调 UI 控件
                    //（否则异常会循环触发断线重连，流量摘要永远归零）
                    Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(L("log.traffic.handle_failed", error.Message)));
                }
            });
    }

    private async Task RefreshProxyBoardAsync()
    {
        if (_mainWindow is not { IsVisible: true, IsProxyTabSelected: true }
            || _processManager?.IsRunning != true)
        {
            return;
        }

        try
        {
            ProxyBoard board = await _proxyBoard!.RefreshAsync();
            _proxyPage?.ApplyBoard(board);
            _proxyProviders?.ApplyProviders(board.Providers);
            _lastProxyRefreshError = null;   // 成功即清除指纹，下次失败可再记录
        }
        catch (Exception error)
        {
            // 连续失败按指纹去重记录（避免每 4 秒刷屏）
            string fingerprint = error.GetType().FullName + ":" + error.Message;
            if (_lastProxyRefreshError != fingerprint)
            {
                _lastProxyRefreshError = fingerprint;
                _mainWindow?.AppendLog(L("log.proxy.board_refresh_failed", error.Message));
            }
        }
    }

    // ---- 远程机器（M5-2：源 RemoteMachineStore/RemoteMachineManagerView/
    //      AppViewModel+RemoteMachine.swift——切换前连通验证、状态清空、只读联动） ----

    /// <summary>当前控制器地址与密钥（远程目标用机器值；本机用默认 127.0.0.1:9090）。</summary>
    private (string Controller, string? Secret) ResolveActiveController() =>
        _remoteStore?.ActiveMachine is { } machine
            ? (machine.ControllerAddress, machine.Secret)
            : (DefaultController, null);

    /// <summary>重建头部目标下拉并选中当前目标（不回环触发切换）。</summary>
    private void RefreshMachineSelector()
    {
        if (_remoteStore == null || _mainWindow == null)
        {
            return;
        }

        string localLabel = L("machine.local_label", DefaultController);
        _mainWindow.SetMachineTargets(
            localLabel,
            _remoteStore.Machines.Select(machine => (machine.Id, machine.Name)).ToList(),
            _remoteStore.ActiveMachineId);
        UpdateMachineStatusDot(null);
    }

    /// <summary>头部状态点（当前目标的连通态；本机跟随 API 健康，远程跟随机器探测）。</summary>
    private void UpdateMachineStatusDot(Guid? _)
    {
        if (_mainWindow == null || _remoteStore == null)
        {
            return;
        }

        if (_remoteStore.ActiveMachine is { } machine)
        {
            MachineConnectionStatus status = _remoteStore.StatusOf(machine.Id);
            (string color, string text) = status.Kind switch
            {
                MachineConnectionKind.Checking => ("#7FA8D9", L("machine.tooltip.checking")),
                MachineConnectionKind.Connected => ("#34A853", L("machine.tooltip.connected", status.Version ?? "OK")),
                MachineConnectionKind.Failed => ("#D93025", L("machine.tooltip.failed", status.Reason ?? L("machine.tooltip.unknown"))),
                _ => ("#B0B7C0", L("machine.tooltip.unknown")),
            };
            _mainWindow.SetMachineStatusDot(color, text);
        }
        else
        {
            bool healthy = _poller?.Status == ApiHealth.Healthy;
            _mainWindow.SetMachineStatusDot(
                healthy ? "#34A853" : "#B0B7C0",
                healthy ? L("machine.tooltip.local_healthy") : L("machine.tooltip.local_unhealthy"));
        }
    }

    /// <summary>远程目标的只读联动（订阅/守护/覆写/内核动作禁用 + 提示条）。</summary>
    private void ApplyRemoteReadonlyState()
    {
        bool isRemote = _remoteStore?.IsRemoteTarget == true;
        _mainWindow?.SetRemoteReadonly(isRemote);
        _tray?.SetRemoteDisabled(isRemote);   // 定稿：远程目标下托盘内核操作组整组置灰
    }

    /// <summary>
    /// 切换机器目标（源：switchToMachineTarget——远程先连通验证，失败不切换；
    /// 成功后停流清数据、API 跟随新目标、快照重拉；切回本机时补拉内核）。
    /// </summary>
    private async Task SwitchMachineTargetAsync(Guid? machineId)
    {
        if (_isSwitchingMachine || _remoteStore == null)
        {
            return;
        }

        RemoteMachine? machine = machineId is { } id
            ? _remoteStore.Machines.FirstOrDefault(item => item.Id == id)
            : null;
        if (machineId is not null && machine is null)
        {
            return;
        }

        if (machine is not null)
        {
            MachineConnectionStatus status = await _remoteStore.RefreshConnectivityAsync(machine).ConfigureAwait(true);
            if (!status.IsConnected)
            {
                _mainWindow?.AppendLog(L(
                    "log.machine.unreachable",
                    machine.Name,
                    machine.DisplayAddress,
                    status.Reason ?? L("machine.tooltip.unknown")));
                Dispatcher.BeginInvoke(RefreshMachineSelector);   // 下拉回滚到当前目标
                return;
            }
        }

        _isSwitchingMachine = true;
        _mainWindow?.SetMachineSwitching(true);
        try
        {
            _remoteStore.SelectTarget(machineId);
            (string controller, string? secret) = ResolveActiveController();
            _apiClient?.UpdateCredentials(controller, secret);
            ClearPresentationForTargetSwitch();

            _coreSettingsLoaded = false;   // 新目标快照待拉（源：lastSyncedEditableSettings = nil）
            if (machine is null)
            {
                _mainWindow?.AppendLog(L("log.remote.switched_local"));
                if (_processManager?.IsRunning != true)
                {
                    _ = StartCoreAtStartupAsync();   // 本机内核未跑则补拉（源：local 分支的启动恢复）
                }
            }
            else
            {
                _mainWindow?.AppendLog(L("log.remote.switched_remote", machine.Name, machine.DisplayAddress));
            }

            Dispatcher.BeginInvoke(() =>
            {
                RefreshMachineSelector();
                ApplyRemoteReadonlyState();
                RefreshPresentationState();
            });
        }
        finally
        {
            _isSwitchingMachine = false;
            _mainWindow?.SetMachineSwitching(false);
        }
    }

    /// <summary>切换目标后清空各页数据并重启流（源：cancelPolling + 各 store 清空 + 重连）。</summary>
    private void ClearPresentationForTargetSwitch()
    {
        if (_streams != null)
        {
            _streams.CancelAll();
            StartTrafficStream();
            StartMemoryStream();
            StartConnectionsStream();
            StartLogsStream();
        }

        _proxyPage?.Groups.Clear();
        _rulesPage?.Rows.Clear();
        _rulesPage?.PolicyOptions.Clear();
        _connectionsPage?.Connections.Clear();
        _connectionsPage?.StatisticsRows.Clear();
        _logsPage?.ClearAll();
    }

    /// <summary>打开机器管理窗（模态；源：RemoteMachineManagerView 面板）。</summary>
    private void OpenRemoteMachineManager()
    {
        if (_remoteStore == null)
        {
            return;
        }

        var window = new RemoteMachineManagerWindow(_remoteStore, () => DefaultController)
        {
            Owner = _mainWindow,
        };
        window.SwitchTargetRequested += machineId => _ = SwitchMachineTargetAsync(machineId);
        window.MachineSaved += machine =>
        {
            // 新增或更新（编辑既有 id 时走 update；源：save 分支）
            if (_remoteStore!.Machines.Any(item => item.Id == machine.Id))
            {
                _remoteStore.UpdateMachine(machine);
                _mainWindow?.AppendLog(L("log.remote.machine_updated", machine.Name));
            }
            else
            {
                _remoteStore.AddMachine(machine);
                _mainWindow?.AppendLog(L("log.remote.machine_added", machine.Name));
            }
        };
        window.MachineDeleted += id =>
        {
            string? name = _remoteStore!.Machines.FirstOrDefault(item => item.Id == id)?.Name;
            _remoteStore.RemoveMachine(id);
            _mainWindow?.AppendLog(L("log.remote.machine_deleted", name ?? id.ToString()));
            Dispatcher.BeginInvoke(RefreshMachineSelector);
        };
        window.ShowDialog();
    }

    /// <summary>
    /// 打开控制器 WebUI（源：openControllerWebUI）：已配置 external-ui 时指向 {controller}/ui，
    /// 否则打开 metacubexd setup 引导页（带 controller 连接参数——「未配置的引导」对应物）。
    /// </summary>
    private void OpenControllerWebUI()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_controllerUiUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.webui.open_failed", error.Message));
        }
    }

    /// <summary>external-ui 判定与入口地址刷新（源：applyExternalUIConfiguration + makeControllerUIURL）。</summary>
    private void ApplyControllerUiUrl(ConfigSnapshot snapshot)
    {
        (string controller, string? secret) = ResolveActiveController();
        bool configured = !string.IsNullOrWhiteSpace(snapshot.ExternalUiUrl);
        _controllerUiUrl = ControllerWebUiUrl.Build(
            controller, secret, configured, snapshot.ExternalUiName);
    }

    /// <summary>L10n 快捷取文案（日志与动态消息统一走 CoreL10n 字典）。</summary>
    private static string L(string key, params object?[] args) =>
        ClashBarX.Core.L10n.CoreL10n.T(key, args);

    // ---- 应用更新（M5-4：源 AppViewModel+Maintenance.swift 的 currentAppVersionText/
    //      refreshLatestAppRelease + MenuBarCommonViews footer） ----

    /// <summary>当前应用版本（源：currentAppVersionText——程序集产品版本，缺省 0.0.1）。</summary>
    private static string CurrentAppVersionText() =>
        typeof(App).Assembly.GetName().Version?.ToString(3) is { Length: > 0 } version ? version : "0.0.1";

    /// <summary>启动自动检查（源：refreshLatestAppRelease——失败静默、同结果不刷）。</summary>
    private async Task CheckUpdateAtStartupAsync()
    {
        try
        {
            AppReleaseInfo? latest = await _releaseService!
                .FetchLatestWindowsReleaseAsync(CurrentAppVersionText())
                .ConfigureAwait(true);
            Dispatcher.BeginInvoke(() => _settingsPage?.ApplyLatestRelease(latest));
        }
        catch (Exception)
        {
            // 源：catch 后仅在未取消时静默（无 UI 反馈）
        }
    }

    // ---- SSID 策略（M5-3：源 AppViewModel+SSIDStrategy.swift + SSIDMonitorService.swift。
    //      Windows 读 SSID 走 WLAN API（WlanSsidMonitor，P/Invoke 隔离在 App 层），
    //      无 macOS 的定位授权门槛；命中即热重载目标配置） ----

    private void SetupSsidStrategy()
    {
        AppSettings saved = _settingsStore.Load();
        _ssidMonitor = new WlanSsidMonitor();
        _ssidMonitor.SsidChanged += ssid =>
            Dispatcher.BeginInvoke(() => OnSsidChanged(ssid));

        _ssidStrategy = new SsidStrategySectionViewModel();
        _ssidStrategy.RestoreEnabled(saved.SsidStrategyEnabled);
        _ssidStrategy.ApplyBindings(LoadSsidStrategyRules());
        _ssidStrategy.EnabledToggled += enabled =>
        {
            // 源：handleSSIDStrategyEnabledStateChanged——开即监听并应用，关即停
            SaveCurrentSettings();
            if (enabled)
            {
                _ssidMonitor?.Start();
                _ = ApplySsidStrategyForCurrentSsidAsync();
            }
            else
            {
                _ssidStrategyLastLogFingerprint = null;
                _ssidMonitor?.Stop();
            }
        };
        _ssidStrategy.BindCurrentRequested += OnBindCurrentSsidRequested;
        _ssidStrategy.RemoveBindingRequested += ssid =>
        {
            // 源：removeSSIDStrategyBinding
            IReadOnlyList<SsidStrategyRule> next = SsidStrategyRule.Remove(ssid, LoadSsidStrategyRules());
            SaveSsidStrategyRules(next);
            _mainWindow?.AppendLog(L("log.ssid.unbound", ssid));
            _ = ApplySsidStrategyForCurrentSsidAsync();
        };
        _mainWindow!.SetSsidStrategySection(_ssidStrategy);

        if (saved.SsidStrategyEnabled)
        {
            _ssidMonitor.Start();
        }
    }

    /// <summary>SSID 变化（源：handleSSIDMonitorSnapshot——回填显示并触发策略应用）。</summary>
    private void OnSsidChanged(string? ssid)
    {
        _ssidStrategy?.ApplyCurrentSsid(ssid);
        if (_ssidStrategy?.IsEnabled == true && _remoteStore?.IsRemoteTarget != true)
        {
            _ = ApplySsidStrategyForCurrentSsidAsync();
        }
    }

    /// <summary>绑定当前 SSID 到当前选中配置（源：toggleCurrentSSIDBinding——同绑定则解绑）。</summary>
    private void OnBindCurrentSsidRequested()
    {
        // 源：guard !isRemoteTarget
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;
        }

        if (_ssidStrategy?.IsEnabled != true)
        {
            LogSsidStrategyOnce("warning", "enable-first", L("log.ssid.enable_first"));
            return;
        }

        string? currentSsid = _ssidMonitor?.PublishCurrentSsid();
        if (string.IsNullOrWhiteSpace(currentSsid))
        {
            _mainWindow?.AppendLog(L("log.ssid.ssid_unavailable"));
            return;
        }

        string? selectedConfig = _subscriptions?.GetSelectedFileName();
        if (string.IsNullOrWhiteSpace(selectedConfig))
        {
            _mainWindow?.AppendLog(L("log.ssid.no_selected_config"));
            return;
        }

        IReadOnlyList<SsidStrategyRule> rules = LoadSsidStrategyRules();
        if (rules.Any(rule => rule.Ssid == currentSsid && rule.ConfigFileName == selectedConfig))
        {
            // 源：重复绑定 → 解除（toggle 语义）
            IReadOnlyList<SsidStrategyRule> next = SsidStrategyRule.Remove(currentSsid, rules);
            SaveSsidStrategyRules(next);
            _mainWindow?.AppendLog(L("log.ssid.unbound", currentSsid));
            return;
        }

        IReadOnlyList<SsidStrategyRule> upserted = SsidStrategyRule.Upsert(currentSsid, selectedConfig, rules);
        SaveSsidStrategyRules(upserted);
        _mainWindow?.AppendLog(L("log.ssid.bound", currentSsid, selectedConfig));
        _ = ApplySsidStrategyForCurrentSsidAsync();
    }

    /// <summary>
    /// 命中应用（源：applySSIDStrategyForCurrentSSIDIfNeeded + resolveSSIDStrategyConfig）：
    /// noAction 清指纹；missingConfig 指纹告警；switchToConfig 热重载 + banner 对应物。
    /// </summary>
    private async Task ApplySsidStrategyForCurrentSsidAsync()
    {
        if (_ssidStrategy?.IsEnabled != true || _remoteStore?.IsRemoteTarget == true)
        {
            return;
        }

        string? currentSsid = _ssidMonitor?.QueryCurrentSsid();
        string? currentConfigName = _subscriptions?.GetSelectedFileName();
        IReadOnlyList<string> available = _subscriptions?.ListConfigFiles() ?? new List<string>();
        SsidStrategyResolution resolution = SsidStrategyResolver.Resolve(
            currentSsid, currentConfigName, LoadSsidStrategyRules(), available);

        switch (resolution.Kind)
        {
            case SsidStrategyResolutionKind.NoAction:
                _ssidStrategyLastLogFingerprint = null;
                break;

            case SsidStrategyResolutionKind.MissingConfig:
                LogSsidStrategyOnce(
                    "warning",
                    $"missing-config:{resolution.ConfigFileName}",
                    L("log.ssid.config_missing", resolution.ConfigFileName));
                break;

            case SsidStrategyResolutionKind.SwitchToConfig:
                _ssidStrategyLastLogFingerprint = null;
                string ssidDisplay = string.IsNullOrWhiteSpace(currentSsid) ? L("log.ssid.unknown_wifi") : currentSsid!;
                _mainWindow?.AppendLog(L("log.ssid.switching", ssidDisplay, resolution.ConfigFileName));
                _subscriptions!.Select(resolution.ConfigFileName!);
                RefreshConfigList();
                await ApplySelectedConfigAsync();
                // 源：StatusItemBanner（wifi.circle.fill）的 Windows 对应物：tooltip 闪现
                _tray?.ShowTransientMessage(L("log.ssid.banner", ssidDisplay, resolution.ConfigFileName));
                break;
        }
    }

    private IReadOnlyList<SsidStrategyRule> LoadSsidStrategyRules()
    {
        try
        {
            string json = _settingsStore.Load().SsidStrategyRulesJson;
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<SsidStrategyRule>();
            }

            return SsidStrategyRule.Normalize(
                System.Text.Json.JsonSerializer.Deserialize<List<SsidStrategyRule>>(json) ?? new());
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<SsidStrategyRule>();   // 损坏 JSON 按空规则处理
        }
    }

    private void SaveSsidStrategyRules(IReadOnlyList<SsidStrategyRule> rules)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(rules);
        _settingsStore.Save(_settingsStore.Load() with { SsidStrategyRulesJson = json });
        _ssidStrategy?.ApplyBindings(rules);
    }

    /// <summary>指纹去重日志（源：logSSIDStrategyOnce）。</summary>
    private void LogSsidStrategyOnce(string level, string fingerprint, string message)
    {
        if (_ssidStrategyLastLogFingerprint == fingerprint)
        {
            return;
        }

        _ssidStrategyLastLogFingerprint = fingerprint;
        _mainWindow?.AppendLog(message);
        _ = level;   // 级别区分保留字段（当前只有 warning 路径使用）
    }

    // ---- TUN（M5-1：源 AppViewModel+Tun.swift + ProxyTabView 的 tunModeQuickToggleRow/tunStackMenu。
    //      权限语义：macOS 对 mihomo 二进制 setuid root；Windows 无 setuid，TUN 需内核进程持
    //      管理员令牌（内核由本应用拉起、令牌继承），故收敛为「应用以管理员身份运行」——
    //      未提权开启时经 UAC 重启引导，拒绝/失败如实反馈并回滚开关意图） ----

    private void SetupTunSection()
    {
        AppSettings saved = _settingsStore.Load();
        _tunControl = new TunControlService(_apiClient!);
        // 源：isTunToggleEnabled = (isRemoteTarget || isRuntimeRunning) && !isCoreActionProcessing && !isTunSyncing
        _tunSection = new TunSectionViewModel(
            () => _remoteStore?.IsRemoteTarget == true || _processManager?.IsRunning == true);
        // 启动初值来自持久化快照（源：overlay）；运行态待 API 就绪后校验（restore 链路）
        _tunSection.ApplySnapshot(saved.TunEnabled, saved.TunStack);
        _tunSection.ToggleRequested += enable => _ = OnTunToggleRequestedAsync(enable);
        _tunSection.StackSelected += stack => _ = OnTunStackSelectedAsync(stack);
        _mainWindow!.SetTunSection(_tunSection);
        // 托盘 TUN 二级菜单随开关/stack 变化同步（勾选态 + 当前项 + mips 门槛）
        _tunSection.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(SyncTrayTunState);
        SyncTrayTunState();
        RefreshTunMipsSupport();
    }

    /// <summary>托盘 TUN 二级菜单同步（勾选/单选/mips 门槛随内核版本）。</summary>
    private void SyncTrayTunState() =>
        _tray?.SetTunMenuState(
            _tunSection?.IsTunEnabled == true,
            _tunSection?.SelectedStack,
            TunControlService.IsMipsStackSupported(_poller?.Version));

    /// <summary>mips stack 门槛随内核版本刷新（源：isMipsStackSupported；版本未知时不支持）。</summary>
    private void RefreshTunMipsSupport()
    {
        _tunSection?.SetMipsSupport(TunControlService.IsMipsStackSupported(_poller?.Version));
        SyncTrayTunState();
    }

    /// <summary>开关请求（源：toggleTunMode——防重入、PATCH、GET 验证、状态回填与日志）。</summary>
    private async Task OnTunToggleRequestedAsync(bool enable)
    {
        if (_tunSection is not { IsTunSyncing: false })
        {
            return;
        }

        // 源：if enabled, !isRemoteTarget { ensureTunPermissions }（远程目标免本机提权）
        if (enable && _remoteStore?.IsRemoteTarget != true && !WindowsElevationService.IsCurrentProcessElevated())
        {
            await PromptElevationForTunAsync(enable: true, requestedStack: null);
            return;
        }

        await RunTunPatchAsync(enable, stack: null, reason: "user toggle");
    }

    /// <summary>stack 选择请求（源：selectTunStack——mips 门槛、开态判等防抖、PATCH+验证）。</summary>
    private async Task OnTunStackSelectedAsync(string stack)
    {
        if (_tunSection is not { IsTunSyncing: false })
        {
            return;
        }

        if (stack.Equals("mips", StringComparison.OrdinalIgnoreCase)
            && !TunControlService.IsMipsStackSupported(_poller?.Version))
        {
            // 源：banner 警告 + log warning + return（Windows 对应物：tooltip 闪现）
            string detail = L("log.tun.mips_requires", TunControlService.MipsStackMinimumCoreVersion);
            _mainWindow?.AppendLog(L("log.tun.mips_unsupported", detail));
            _tray?.ShowTransientMessage(detail);
            RollbackTunSelection();
            return;
        }

        // 源：isTunEnabled 且 stack 与当前一致（忽略大小写）时跳过
        if (_tunSection.IsTunEnabled
            && string.Equals(_tunSection.SelectedStack, stack, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_remoteStore?.IsRemoteTarget != true && !WindowsElevationService.IsCurrentProcessElevated())
        {
            await PromptElevationForTunAsync(enable: true, requestedStack: stack);
            return;
        }

        await RunTunPatchAsync(enable: true, stack, reason: "stack switch");
    }

    /// <summary>
    /// UAC 重启引导（Windows 特有；源对应物：ensureTunPermissions 的管理员授权）：
    /// 先落盘开关意图（新实例启动恢复链路自动开启 TUN），再拆内核与系统代理
    /// （避免新旧实例内核端口竞态），runas 提权启动新实例后退出。拒绝/失败回滚意图。
    /// </summary>
    private async Task PromptElevationForTunAsync(bool enable, string? requestedStack)
    {
        var choice = System.Windows.MessageBox.Show(
            _mainWindow,
            L("log.tun.elevation_prompt"),
            L("log.tun.elevation_prompt_title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
        {
            _mainWindow?.AppendLog(L("log.tun.auth_cancelled"));
            RollbackTunSelection();
            return;
        }

        if (requestedStack is null)
        {
            requestedStack = await ResolvePreferredTunStackAsync().ConfigureAwait(true);
        }

        AppSettings saved = _settingsStore.Load();
        _settingsStore.Save(saved with { TunEnabled = true, TunStack = NormalizeTunStack(requestedStack) });

        if (!WindowsElevationService.TryRestartAsAdministrator(out string? failureMessage))
        {
            // 提权失败回滚开关与 stack 意图（评审 P1：TunStack 未回滚会静默改写偏好）
            _settingsStore.Save(_settingsStore.Load() with { TunEnabled = saved.TunEnabled, TunStack = saved.TunStack });
            _mainWindow?.AppendLog(L("log.tun.elevation_failed", failureMessage ?? L("log.tun.unknown_error")));
            RollbackTunSelection();
            return;
        }

        // 先拆内核与系统代理再退出（新实例拉内核时旧内核必然已死，规避 9090 端口竞态）
        if (_systemProxy?.ExpectedTarget != null)
        {
            _systemProxy.Disable();
        }

        _processManager?.StopImmediately();
        Shutdown();
    }

    /// <summary>TUN PATCH 主链路（源：toggleTunMode/selectTunStack 的 try 段）：PATCH → 验证 → 回填 → 持久化。</summary>
    private async Task RunTunPatchAsync(bool enable, string? stack, string reason)
    {
        _tunSection!.SetSyncing(true);
        try
        {
            string? effectiveStack = stack;
            if (enable && string.IsNullOrWhiteSpace(effectiveStack))
            {
                effectiveStack = await ResolvePreferredTunStackAsync().ConfigureAwait(true);
            }

            TunToggleResult result = await _tunControl!
                .ToggleAsync(enable, effectiveStack)
                .ConfigureAwait(true);
            _tunSection.ApplySnapshot(result.State.Enabled, result.State.Stack);
            PersistTunState(result.State);
            if (result.Outcome == TunToggleOutcome.Applied)
            {
                string message = stack is null
                    ? L("log.tun.toggled", result.State.Enabled ? L("log.tun.enabled") : L("log.tun.disabled"))
                    : L("log.tun.stack_changed", result.State.Stack ?? string.Empty);
                _mainWindow?.AppendLog(message);
            }
            else
            {
                // 源：runtime_state_mismatch → log error（不抛）
                _mainWindow?.AppendLog(L("log.tun.state_mismatch"));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.tun.toggle_failed", reason, error.Message));
            await RefreshTunFromRuntimeAsync().ConfigureAwait(true);
        }
        finally
        {
            _tunSection.SetSyncing(false);
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
    }

    /// <summary>偏好 stack（源：preferredTunStack）：保存值优先；配置自带 stack 时不注入；否则 mixed。</summary>
    private async Task<string?> ResolvePreferredTunStackAsync()
    {
        AppSettings saved = _settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(saved.TunStack))
        {
            return saved.TunStack.Trim();
        }

        string? configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
        try
        {
            if (File.Exists(configPath)
                && TunYamlStackScanner.DeclaresTunStack(await File.ReadAllTextAsync(configPath).ConfigureAwait(true)))
            {
                return null;   // 配置已声明 stack，交内核使用配置值
            }
        }
        catch (IOException)
        {
            // 读取失败按未声明处理（用默认值兜底）
        }

        return TunControlService.DefaultStack;
    }

    /// <summary>启动恢复（源：validateTunPermissionsOnStartup + verifyTunAfterOverlayIfNeeded +
    /// ensureTunStackOnStartupIfNeeded 的组合）：API 就绪后执行一次。远程目标免本机提权判定。</summary>
    private async Task RestoreTunAtStartupAsync()
    {
        AppSettings saved = _settingsStore.Load();
        if (!saved.TunEnabled)
        {
            return;
        }

        if (_remoteStore?.IsRemoteTarget != true && !WindowsElevationService.IsCurrentProcessElevated())
        {
            // 源：权限失败 → 关闭 TUN + 持久化 + warning（Windows：未以管理员运行）
            try
            {
                if (_processManager?.IsRunning == true)
                {
                    await _tunControl!.ToggleAsync(enable: false).ConfigureAwait(true);
                }
            }
            catch (Exception)
            {
                // 关闭失败不阻断启动（源：try? patchTunConfig）
            }

            _settingsStore.Save(_settingsStore.Load() with { TunEnabled = false });
            _tunSection?.ApplySnapshot(enabled: false, saved.TunStack);
            _mainWindow?.AppendLog(
                L("log.tun.needs_admin"));
            Dispatcher.BeginInvoke(RefreshPresentationState);
            return;
        }

        try
        {
            TunToggleResult result = await _tunControl!
                .RestoreAtStartupAsync(savedEnabled: true, await ResolvePreferredTunStackAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _tunSection?.ApplySnapshot(result.State.Enabled, result.State.Stack);
            PersistTunState(result.State);
            if (result.Outcome == TunToggleOutcome.Applied)
            {
                _mainWindow?.AppendLog(L("log.tun.restored"));
                await TryAlignTunStackAsync().ConfigureAwait(true);
            }
            else
            {
                _mainWindow?.AppendLog(L("log.tun.restore_mismatch"));
                await RefreshTunFromRuntimeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.tun.restore_failed", error.Message));
            await RefreshTunFromRuntimeAsync().ConfigureAwait(true);
        }

        Dispatcher.BeginInvoke(RefreshPresentationState);
    }

    /// <summary>启动 stack 对齐（源：ensureTunStackOnStartupIfNeeded；失败仅记日志）。</summary>
    private async Task TryAlignTunStackAsync()
    {
        try
        {
            string? preferred = await ResolvePreferredTunStackAsync().ConfigureAwait(true);
            if (await _tunControl!.AlignStackIfEnabledAsync(preferred).ConfigureAwait(true))
            {
                TunRuntimeState state = await _tunControl.FetchRuntimeStateAsync().ConfigureAwait(true);
                _tunSection?.ApplySnapshot(state.Enabled, state.Stack);
                PersistTunState(state);
                _mainWindow?.AppendLog(L("log.tun.aligned", state.Stack ?? string.Empty));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.tun.align_failed", error.Message));
        }
    }

    /// <summary>从运行态回读开关与 stack（源：refreshTunStatusFromRuntimeConfig；失败静默）。</summary>
    private async Task RefreshTunFromRuntimeAsync()
    {
        try
        {
            TunRuntimeState state = await _tunControl!.FetchRuntimeStateAsync().ConfigureAwait(true);
            await Dispatcher.BeginInvoke(() =>
            {
                _tunSection?.ApplySnapshot(state.Enabled, state.Stack);
                PersistTunState(state);
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _ = error;   // 回读失败静默（源：catch {}）
        }
    }

    /// <summary>回滚 UI 开关/stack 到当前实际显示前的值（拒绝授权或失败后，抑制事件回放）。</summary>
    private void RollbackTunSelection()
    {
        if (_tunSection == null)
        {
            return;
        }

        AppSettings saved = _settingsStore.Load();
        _tunSection.ApplySnapshot(saved.TunEnabled, saved.TunStack);
        Dispatcher.BeginInvoke(RefreshPresentationState);
    }

    /// <summary>持久化 TUN 实际态（源：persistEditableSettingsSnapshot 的 tun 字段对）。</summary>
    private void PersistTunState(TunRuntimeState state)
    {
        AppSettings saved = _settingsStore.Load();
        _settingsStore.Save(saved with
        {
            TunEnabled = state.Enabled,
            TunStack = string.IsNullOrWhiteSpace(state.Stack) ? saved.TunStack : state.Stack.Trim(),
        });
    }

    private static string NormalizeTunStack(string? stack) =>
        string.IsNullOrWhiteSpace(stack) ? TunControlService.DefaultStack : stack.Trim();

    /// <summary>
    /// 测全部分组（源：refreshAllGroupLatencies——UI 已按 hidden/模式过滤后的分组集，
    /// 并发组测速；进行中态在 VM 上驱动按钮禁用）。
    /// </summary>
    private async Task TestAllGroupsAsync()
    {
        if (_proxyPage is null)
        {
            return;
        }

        _proxyPage.IsTestingAllGroupLatencies = true;
        try
        {
            List<ProxyGroupViewModel> groups = _proxyPage.Groups.ToList();
            await Task.WhenAll(groups.Select(group => TestGroupAndApplyAsync(group.Name))).ConfigureAwait(true);
            _mainWindow?.AppendLog(L("log.proxy.test_all_done", groups.Count));
        }
        finally
        {
            _proxyPage.IsTestingAllGroupLatencies = false;
        }
    }

    private async Task TestGroupAndApplyAsync(string groupName)
    {
        try
        {
            IReadOnlyDictionary<string, int> results = await _proxyBoard!
                .TestGroupAsync(groupName)
                .ConfigureAwait(true);
            _proxyPage?.ApplyGroupTestResults(groupName, results);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.proxy.group_test_failed", groupName, error.Message));
        }
    }

    /// <summary>更新全部 provider（源：refreshAllProxyProviders——顶部按钮、进行中态、完成后回填）。</summary>
    private async Task UpdateAllProvidersAsync()
    {
        if (_proxyProviders is null || _proxyProviders.IsRefreshingAll)
        {
            return;
        }

        _proxyProviders.SetRefreshingAll(true);
        try
        {
            IReadOnlyList<string> updated = await _proxyBoard!.UpdateAllProvidersAsync().ConfigureAwait(true);
            _mainWindow?.AppendLog(L("log.provider.update_all_done", updated.Count));
            ProxyBoard board = await _proxyBoard.RefreshAsync().ConfigureAwait(true);
            _proxyProviders.ApplyProviders(board.Providers);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.provider.update_all_failed", error.Message));
        }
        finally
        {
            _proxyProviders.SetRefreshingAll(false);
        }
    }

    /// <summary>更新单个 provider（源：updateProxyProvider——行级刷新、更新中态）。</summary>
    private async Task UpdateProviderAsync(string providerName)
    {
        if (_proxyProviders is null)
        {
            return;
        }

        _proxyProviders.SetProviderUpdating(providerName, true);
        try
        {
            await _proxyBoard!.UpdateProviderAsync(providerName).ConfigureAwait(true);
            _mainWindow?.AppendLog(L("log.provider.updated", providerName));
            ProxyBoard board = await _proxyBoard.RefreshAsync().ConfigureAwait(true);
            _proxyProviders.ApplyProviderDetail(
                providerName, board.Providers.GetValueOrDefault(providerName));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.provider.update_failed", providerName, error.Message));
        }
        finally
        {
            _proxyProviders?.SetProviderUpdating(providerName, false);
        }
    }

    /// <summary>provider healthcheck（源：providerActionButton(.healthcheck)）。</summary>
    private async Task HealthcheckProviderAsync(string providerName)
    {
        try
        {
            await _proxyBoard!.HealthcheckProviderAsync(providerName).ConfigureAwait(true);
            _mainWindow?.AppendLog(L("log.provider.healthcheck_submitted", providerName));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _mainWindow?.AppendLog(L("log.provider.healthcheck_failed", providerName, error.Message));
        }
    }

    // ---- 规则页（M4-1：信息架构源 RulesTabView/RulesViewModel；源数据流 refreshProvidersAndRules） ----

    private void SetupRulesPage()
    {
        _rulesBoard = new RulesBoardService(_apiClient!);
        _rulesPage = new RulesPageViewModel(
            () => RefreshRulesAsync(useProvidersRefresh: true),
            () => _processManager?.IsRunning == true);
        _mainWindow!.SetRulesPage(_rulesPage);

        // 规则 tab 激活时的低频轮询（源：refreshLowFrequency 的 rules 前台 20s 间隔）
        _rulesRefreshTimer = new System.Threading.Timer(
            _ => Dispatcher.BeginInvoke(() => _ = RefreshRulesAsync(useProvidersRefresh: false)),
            null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(20));

        SetupRuleOverride();
    }

    // ---- 规则覆写（M4-1b：源 RuleOverrideService/AppViewModel+RuleOverride.swift） ----

    private void SetupRuleOverride()
    {
        _ruleOverride = new RuleOverrideService(
            ClashBarXPaths.OverridesDirectory(), ClashBarXPaths.RuntimeConfigDirectory());
        _ruleOverridePage = new RuleOverrideViewModel(
            _ruleOverride,
            ApplyRuleOverridesAsync,
            () => _processManager?.IsRunning == true,
            message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message)),
            OpenOverridesFolder)
        {
            // 初始化需要读文件，放在 UI 线程同步段（文件小，开销可忽略）
        };
        _ruleOverridePage.Restore(_settingsStore.Load().RuleOverrideEnabled);
        _ruleOverridePage.ReloadFromFilesystem();
        _ruleOverridePage.EnabledToggled += enabled =>
        {
            SaveCurrentSettings();
            if (enabled)
            {
                _ = Dispatcher.InvokeAsync(() => _ = ApplyRuleOverridesAsync());
            }
        };
        _mainWindow!.SetRuleOverridePage(_ruleOverridePage);
    }

    /// <summary>打开覆写目录（不存在则先创建，源：openRuleOverridesFolder）。</summary>
    private void OpenOverridesFolder()
    {
        try
        {
            string directory = ClashBarXPaths.OverridesDirectory();
            _ruleOverride?.EnsureOverridesDirectory();
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.override.open_folder_failed", error.Message));
        }
    }

    /// <summary>
    /// 启动/重启内核的配置解析（源：prepareCoreLaunchConfig）：覆写启用且内容非空时
    /// 生成 runtime 配置（沿用原文件名），工作目录保持原配置目录（相对路径资源解析一致）。
    /// 生成失败抛错不回退（源语义：避免用户误以为覆写已生效）。
    /// </summary>
    private (string LaunchPath, string? WorkingDirectory) ResolveCoreLaunchConfig(string configPath)
    {
        if (!_settingsStore.Load().RuleOverrideEnabled)
        {
            return (configPath, null);
        }

        RuleOverrideContent content = _ruleOverride!.LoadContent();
        if (content.Counts.IsEmpty)
        {
            return (configPath, null);
        }

        string runtimePath = _ruleOverride.WriteRuntimeConfig(configPath, content);
        RuleOverrideCounts counts = content.Counts;
        Dispatcher.BeginInvoke(() =>
        {
            _ruleOverridePage?.MarkApplied(Path.GetFileName(configPath), counts);
            _mainWindow?.AppendLog(L(
                "log.override.runtime_generated",
                Path.GetFileName(runtimePath),
                counts.Prepend,
                counts.Append,
                counts.RuleProviders));
        });
        return (runtimePath, Path.GetDirectoryName(Path.GetFullPath(configPath)));
    }

    /// <summary>运行中应用覆写（源：applyRuleOverridesIfRunning——PUT /configs 推 payload 全文；远程禁用）。</summary>
    private async Task ApplyRuleOverridesAsync()
    {
        if (!_ruleOverridePage?.IsEnabled == true || _remoteStore?.IsRemoteTarget == true)
        {
            return;
        }

        if (_processManager?.IsRunning != true)
        {
            _ruleOverridePage?.MarkPending();
            return;
        }

        try
        {
            string configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
            RuleOverrideContent content = _ruleOverride!.LoadContent();
            if (content.Counts.IsEmpty)
            {
                _ruleOverridePage?.MarkEmpty();
                return;
            }

            string runtimePath = _ruleOverride.WriteRuntimeConfig(configPath, content);
            string payload = await File.ReadAllTextAsync(runtimePath).ConfigureAwait(false);
            await _apiClient!.RequestNoResponseAsync(
                MihomoEndpoint.PutConfigs(force: false, path: runtimePath, payload: payload))
                .ConfigureAwait(false);
            RuleOverrideCounts counts = content.Counts;
            await Dispatcher.BeginInvoke(() =>
            {
                _ruleOverridePage?.MarkApplied(Path.GetFileName(configPath), counts);
                _mainWindow?.AppendLog(L("log.override.applied_running", Path.GetFileName(runtimePath)));
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Dispatcher.BeginInvoke(() =>
            {
                _ruleOverridePage?.MarkFailed(error.Message);
                _mainWindow?.AppendLog(L("log.override.apply_failed", error.Message));
            });
        }
    }

    private async Task RefreshRulesAsync(bool useProvidersRefresh)
    {
        if (_mainWindow is not { IsVisible: true, IsRulesTabSelected: true }
            || _processManager?.IsRunning != true)
        {
            return;
        }

        try
        {
            RulesBoard board = useProvidersRefresh
                ? await _rulesBoard!.RefreshProvidersAsync()
                : await _rulesBoard!.RefreshAsync();
            _rulesPage?.ApplyBoard(board);
            _lastRulesRefreshError = null;
        }
        catch (Exception error)
        {
            // 连续失败按指纹去重记录（照代理页面板刷新的模式）
            string fingerprint = error.GetType().FullName + ":" + error.Message;
            if (_lastRulesRefreshError != fingerprint)
            {
                _lastRulesRefreshError = fingerprint;
                _mainWindow?.AppendLog(L("log.rules.board_refresh_failed", error.Message));
            }
        }
    }

    // ---- 连接页（M3-1：信息架构源 ConnectionsTabView/ConnectionsViewModel） ----

    private void SetupConnectionsPage()
    {
        _connectionStatistics = new ConnectionStatisticsService();
        _connectionsPage = new ConnectionsPageViewModel(
            CloseConnectionAsync,
            CloseAllConnectionsAsync,
            message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message)));
        _mainWindow!.SetConnectionsPage(_connectionsPage);

        // UI 可见性在 UI 线程上缓存成快照（WS 回调线程直接读 WPF 属性会抛跨线程异常）；
        // 统计视图态（VM 普通属性）同样纳入快照，避免 WS 线程与 UI 线程的帧间错配
        _mainWindow.TabSelectionChanged += (_, _) => RefreshConnectionsVisibility();
        _mainWindow.IsVisibleChanged += (_, _) => RefreshConnectionsVisibility();
        _mainWindow.StateChanged += (_, _) => RefreshConnectionsVisibility();
        _connectionsPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectionsPageViewModel.ShowsStatistics)
                or nameof(ConnectionsPageViewModel.StatisticsDimension))
            {
                RefreshConnectionsVisibility();
            }
        };
        RefreshConnectionsVisibility();
        StartConnectionsStream();
        StartMemoryStream();
    }

    /// <summary>memory WS 流（概览内存占用；源：Swift startMemoryStream）。</summary>
    private void StartMemoryStream()
    {
        _streams!.Start(
            "memory",
            () => _apiClient!.CreateWebSocketConnection(MihomoEndpoint.Memory()),
            async payload =>
            {
                try
                {
                    MemorySnapshot? snapshot = JsonSerializer.Deserialize<MemorySnapshot>(
                        payload.Span, JsonSharedOptions.Json);
                    if (snapshot != null)
                    {
                        await Dispatcher.BeginInvoke(() =>
                        {
                            _proxyPage?.UpdateMemory(snapshot.Inuse);
                            _mainWindow?.SetStatusMisc(_proxyPage?.StatusMiscText ?? string.Empty);
                        });
                    }
                }
                catch (JsonException)
                {
                    // 噪声 payload 丢弃
                }
            });
    }

    /// <summary>日志页内存上限档位（仅 UI 线程：窗口可见且非最小化才用 5000 档）。</summary>
    private void UpdateLogsPanelVisibility() =>
        _logsPage?.SetPanelVisible(
            _mainWindow is { IsVisible: true, WindowState: not WindowState.Minimized });

    /// <summary>连接页可见性与统计视图态快照（仅 UI 线程调用：tab 选中 + 窗口可见且非最小化）。</summary>
    private void RefreshConnectionsVisibility()
    {
        _connectionsUiVisible = _mainWindow
            is { IsVisible: true, WindowState: not WindowState.Minimized, IsConnectionsTabSelected: true };
        _connectionsShowsStatistics = _connectionsPage?.ShowsStatistics == true;
        _connectionsStatisticsDimension = _connectionsPage?.StatisticsDimension ?? TrafficStatisticsDimension.Node;
    }

    private void StartConnectionsStream()
    {
        _connectionsProcessMatchingEnabled = false;
        _streams!.Start(
            "connections",
            () => _apiClient!.CreateWebSocketConnection(MihomoEndpoint.Connections(intervalMilliseconds: 1000)),
            async payload =>
            {
                try
                {
                    // 首帧确认 controller 就绪后开启进程识别（应用维度统计前提；源：enableTrafficStatisticsProcessMatching）
                    if (!_connectionsProcessMatchingEnabled)
                    {
                        _connectionsProcessMatchingEnabled = true;
                        try
                        {
                            await _apiClient!.RequestNoResponseAsync(MihomoEndpoint.PatchConfigs(
                                new JsonObject { ["find-process-mode"] = "always" }));
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            Dispatcher.BeginInvoke(() =>
                                _mainWindow?.AppendLog(L("log.connections.process_matching_failed", error.Message)));
                        }
                    }

                    // 可见性感知：列表仅在连接页可见且非统计视图时输出；统计视图喂当前维度
                    //（源：applyConnectionsUpdate 的 includeConnections/statisticsDimension 口径）。
                    // 窗口/tab/统计态全部读 UI 线程维护的快照
                    bool statisticsVisible = _connectionsUiVisible && _connectionsShowsStatistics;
                    bool connectionsVisible = _connectionsUiVisible && !_connectionsShowsStatistics;
                    ConnectionStatisticsUpdate update = _connectionStatistics!.Process(
                        payload,
                        includeConnections: connectionsVisible,
                        statisticsDimension: statisticsVisible ? _connectionsStatisticsDimension : null);
                    await Dispatcher.BeginInvoke(() =>
                    {
                        _connectionsPage?.IngestSnapshot(update);
                        _proxyPage?.UpdateConnectionsCount(update.TotalCount);   // 概览连接数
                        _mainWindow?.SetStatusMisc(_proxyPage?.StatusMiscText ?? string.Empty);
                    });
                }
                catch (JsonException)
                {
                    // 噪声/半包 payload 丢弃（normalize 已滤常见噪声）
                }
                catch (Exception error)
                {
                    // 解码侧意外异常编组到 UI 线程记录，避免在线程池直调 UI 控件后循环触发重连
                    Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(L("log.connections.handle_failed", error.Message)));
                }
            });
    }

    private Task CloseConnectionAsync(string connectionId) =>
        _apiClient!.RequestNoResponseAsync(MihomoEndpoint.CloseConnection(connectionId));

    private Task CloseAllConnectionsAsync() =>
        _apiClient!.RequestNoResponseAsync(MihomoEndpoint.CloseAllConnections());

    // ---- 日志页（M3-2：信息架构源 LogsTabView/LogsViewModel + Logging 管线） ----

    private void SetupLogsPage()
    {
        _logsPage = new LogsPageViewModel();
        _logsPage.LogLevelChanged += level =>
        {
            _settingsPage?.CoreSettings.SyncLogLevel(level);   // 设置页同步显示（不重复 PATCH）
            _ = PatchLogLevelAsync(level);
        };
        _mainWindow!.SetLogsPage(_logsPage);

        // mihomo 日志 150ms 批量刷（源：mihomoLogFlushIntervalNanoseconds = 150ms）
        _mihomoLogFlushTimer = new DispatcherTimer { Interval = LogsPageViewModel.MihomoLogFlushInterval };
        _mihomoLogFlushTimer.Tick += (_, _) => _logsPage.FlushPendingMihomoLogs();
        _mihomoLogFlushTimer.Start();

        // 面板可见性：隐藏或最小化时收紧内存上限到 20（源：hiddenPanelMaxInMemoryLogEntries；
        // 最小化时 IsVisible 不变，需联动 StateChanged——对齐 Swift 面板关闭即停的语义）
        _mainWindow.IsVisibleChanged += (_, _) => UpdateLogsPanelVisibility();
        _mainWindow.StateChanged += (_, _) => UpdateLogsPanelVisibility();
        UpdateLogsPanelVisibility();

        StartLogsStream();
    }

    private void StartLogsStream()
    {
        _streams!.Start(
            "logs",
            () => _apiClient!.CreateWebSocketConnection(MihomoEndpoint.Logs(level: null)),
            async payload =>
            {
                try
                {
                    if (MihomoLogDecoder.TryDecode(payload.Span, out string level, out string message))
                    {
                        await Dispatcher.BeginInvoke(() => _logsPage?.QueueMihomoLog(level, message));
                    }
                }
                catch (Exception error)
                {
                    // 解码侧意外异常编组到 UI 线程记录，避免在线程池直调 UI 后循环触发重连
                    Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(L("log.logs.handle_failed", error.Message)));
                }
            });
    }

    // ---- 代理守护（M3-3：源 AppViewModel+ProxyGuard.swift；CLI 触发的进程间通知
    //      选型为命名 EventWaitHandle——对齐 macOS 的 DistributedNotificationCenter，
    //      语义为「通知运行中实例立即探测；无实例时随应用启动触发」） ----

    /// <summary>守护触发的跨进程命名事件名（对应 macOS 的 com.clashbarx.proxyguard.trigger）。</summary>
    private const string GuardTriggerEventName = "ClashBarX.ProxyGuard.Trigger";

    /// <summary>卸载器通知退出的 CLI 参数与命名事件（源：UninstallRun 的 --exit）。</summary>
    private const string ExitArgument = "--exit";
    private const string ExitEventName = "ClashBarX.Exit";

    private void SetupProxyGuard()
    {
        AppSettings settings = _settingsStore.Load();
        _guardIntervalSeconds = Math.Max(ProxyGuardService.MinimumIntervalSeconds, settings.ProxyGuardIntervalSeconds);

        _proxyGuard = new ProxyGuardService(
            _apiClient!,
            ProbeViaCoreProxyAsync,
            () => _guardIntervalSeconds,
            // 源：guard isRuntimeRunning, !isRemoteTarget（远程目标不做本机守护探测）
            canProbe: () => _processManager?.IsRunning == true
                && _remoteStore?.IsRemoteTarget != true
                && _poller?.Status == ApiHealth.Healthy);
        // Logged 与 ProbeCompleted 来自后台线程，统一编组到 UI
        _proxyGuard.Logged += (level, message) =>
            Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[guard] {message}"));
        _proxyGuard.ProbeCompleted += state =>
            Dispatcher.BeginInvoke(() => _guardSettings?.ApplyProbeState(state));

        _guardSettings = new ProxyGuardSettingsViewModel(CheckGuardNowAsync, () => _processManager?.IsRunning == true);
        _guardSettings.Restore(settings.ProxyGuardEnabled, _guardIntervalSeconds);
        _guardSettings.GuardToggled += OnGuardToggled;
        _guardSettings.IntervalChanged += seconds =>
        {
            _guardIntervalSeconds = seconds;
            SaveCurrentSettings();
        };
        _mainWindow!.SetGuardSettings(_guardSettings);

        if (settings.ProxyGuardEnabled)
        {
            // 仅按持久化开关启动（CLI 触发不越过用户开关——源：startProxyGuardIfNeeded 的
            // guard proxyGuardEnabled 语义）；开启态下 CLI 触发首启先探一次
            _proxyGuard.Start(probeImmediately: _cliProxyGuardTrigger);
        }

        StartGuardTriggerListener();
        StartExitListener();
    }

    /// <summary>通知运行中的实例退出（--exit CLI；无实例时静默）。</summary>
    private static void TryNotifyExitToRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle? existing))
        {
            try
            {
                existing.Set();
            }
            finally
            {
                existing.Dispose();
            }
        }
    }

    /// <summary>监听卸载器的退出通知事件。</summary>
    private void StartExitListener()
    {
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _ = Task.Run(() =>
        {
            while (true)
            {
                bool signaled;
                try
                {
                    signaled = exitEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!signaled)
                {
                    return;
                }

                Dispatcher.BeginInvoke(Shutdown);
            }
        });
    }

    /// <summary>探测：经内核混合端口对 204 URL 发 HEAD（源：probeConnectivity 的 127.0.0.1:{mixedPort} 口径）。</summary>
    private async Task<bool> ProbeViaCoreProxyAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConfigSnapshot config = await _apiClient!.RequestAsync<ConfigSnapshot>(
                MihomoEndpoint.GetConfigs(), cancellationToken);
            int mixedPort = config.MixedPort ?? 0;
            return mixedPort > 0
                && await ProxyGuardProbe.ProbeAsync(ProxyGuardService.DefaultProbeUrl, mixedPort, null, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;   // 探测失败视为不可用（源：非 2xx / 超时 / 错误均失败）
        }
    }

    private async Task CheckGuardNowAsync()
    {
        if (_proxyGuard is null)
        {
            return;
        }

        // 立即检查时守护可能未运行（开关关）：临时启动一次探测
        if (!_proxyGuard.IsRunning)
        {
            _proxyGuard.Start();
        }

        await _proxyGuard.ProbeNowAsync();
    }

    private void OnGuardToggled(bool enabled)
    {
        if (enabled)
        {
            _proxyGuard?.Start();
        }
        else
        {
            _proxyGuard?.Stop();
        }

        SaveCurrentSettings();
    }

    /// <summary>合并当前守护与覆写设置后落盘（系统代理开关等其他字段保持已存值）。</summary>
    private void SaveCurrentSettings()
    {
        AppSettings saved = _settingsStore.Load();
        _settingsStore.Save(saved with
        {
            ProxyGuardEnabled = _guardSettings?.IsGuardEnabled ?? saved.ProxyGuardEnabled,
            ProxyGuardIntervalSeconds = _guardIntervalSeconds,
            RuleOverrideEnabled = _ruleOverridePage?.IsEnabled ?? saved.RuleOverrideEnabled,
            SystemProxyBypassList = _systemProxySection?.NormalizedExceptions() ?? saved.SystemProxyBypassList,
            AppearanceMode = _settingsPage?.AppearanceMode switch
            {
                AppAppearanceMode.Light => "light",
                AppAppearanceMode.Dark => "dark",
                _ => "system",
            },
            Language = _settingsPage?.Language == AppLanguage.English ? "en" : "zh-Hans",
            LaunchAtLogin = _settingsPage?.LaunchAtLogin ?? saved.LaunchAtLogin,
            ShowTrafficOverview = _settingsPage?.ShowTrafficOverview ?? saved.ShowTrafficOverview,
            CloseConnectionsOnSwitch = _settingsPage?.CoreSettings.CloseConnections ?? saved.CloseConnectionsOnSwitch,
            TunEnabled = _tunSection?.IsTunEnabled ?? saved.TunEnabled,
            TunStack = string.IsNullOrWhiteSpace(_tunSection?.SelectedStack) ? saved.TunStack : _tunSection!.SelectedStack!,
            SsidStrategyEnabled = _ssidStrategy?.IsEnabled ?? saved.SsidStrategyEnabled,
        });
    }

    /// <summary>
    /// 通知运行中的其他实例立即探测（源：DistributedNotificationCenter 触发分支）。
    /// 返回是否成功通知到既有实例——事件为内核对象，实例退出即销毁，存在即视为活着。
    /// </summary>
    private bool TryTriggerRunningGuardInstance()
    {
        if (EventWaitHandle.TryOpenExisting(GuardTriggerEventName, out EventWaitHandle? existing))
        {
            try
            {
                return existing.Set();
            }
            finally
            {
                existing.Dispose();
            }
        }

        return false;
    }

    /// <summary>监听命名事件：CLI 或其他实例触发时立即探测。</summary>
    private void StartGuardTriggerListener()
    {
        _guardTriggerEvent = new EventWaitHandle(false, EventResetMode.AutoReset, GuardTriggerEventName);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                bool signaled;
                try
                {
                    signaled = _guardTriggerEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!signaled)
                {
                    return;
                }

                if (_proxyGuard is { IsRunning: true } guard)
                {
                    await guard.ProbeNowAsync().ConfigureAwait(false);
                }
            }
        });
    }

    // ---- 订阅管理（M2-3） ----

    /// <summary>模态反馈（源：runModalAlert 的 Windows 对应物；UI 线程调用）。</summary>
    private void ShowMessage(string title, string message, bool isError) => System.Windows.MessageBox.Show(
        _mainWindow,
        message,
        title,
        System.Windows.MessageBoxButton.OK,
        isError ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);

    private void RefreshConfigList()
    {
        if (_subscriptions == null || _mainWindow == null)
        {
            return;
        }

        IReadOnlyList<string> files = _subscriptions.ListConfigFiles();
        string? selected = _subscriptions.GetSelectedFileName();
        _mainWindow.SetConfigFiles(files, selected);
    }

    /// <summary>内核是否已放置（no-core 策略：%APPDATA%\ClashBarX\core\mihomo.exe）。</summary>
    private bool IsCoreBinaryPresent() =>
        _processManager?.DetectedBinaryPath is { } binaryPath && File.Exists(binaryPath);

    private async void OnSubscriptionImportRequested(string urlText)
    {
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;   // 源：远程目标配置管理只读（UI 已禁用，此处兜底）
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url))
        {
            string invalidUrl = L("log.subscription.invalid_url");
            _mainWindow?.AppendLog(invalidUrl);
            ShowMessage(L("guide.import_failed.title"), invalidUrl, isError: true);
            return;
        }

        // 首次导入（此前没有任何配置文件）给引导性反馈（源：app.config.remote_import.alert.success）
        int configCountBeforeImport = _subscriptions!.ListConfigFiles().Count;
        bool corePresent = IsCoreBinaryPresent();
        _mainWindow?.SetImportBusy(true);
        try
        {
            // UA 携带 clash.meta/当前内核版本（源：remoteSubscriptionUserAgent）
            string version = _poller?.Version is string current && current.Length > 0
                ? current
                : DefaultMihomoVersionForUserAgent;
            SubscriptionInfo info = await _subscriptions.AddAsync(url, $"clash.meta/{version}");

            _subscriptions.Select(info.FileName);
            _mainWindow?.AppendLog(L("log.subscription.imported", info.FileName));
            RefreshConfigList();
            if (FirstRunGuidePolicy.ShouldAnnounceImportSuccess(configCountBeforeImport, corePresent))
            {
                ShowMessage(
                    L("guide.import_success.title"),
                    L("guide.import_success.message", info.FileName),
                    isError: false);
            }

            await ApplySelectedConfigAsync();
        }
        catch (Exception error)
        {
            string message = L("log.subscription.import_failed", error.Message);
            _mainWindow?.AppendLog(message);
            ShowMessage(L("guide.import_failed.title"), message, isError: true);   // 失败原因只进日志页用户看不到
        }
        finally
        {
            _mainWindow?.SetImportBusy(false);
        }
    }

    /// <summary>导入本地配置文件（源：订阅「导入本地文件」——拷贝进 configs 目录并启用）。</summary>
    private async void OnLocalImportRequested(string sourcePath)
    {
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;   // 远程目标配置管理只读（UI 已禁用，此处兜底）
        }

        int configCountBeforeImport = _subscriptions?.ListConfigFiles().Count ?? 0;
        bool corePresent = IsCoreBinaryPresent();
        try
        {
            string fileName = Path.GetFileName(sourcePath);
            string configsDirectory = Path.Combine(ClashBarXPaths.AppDataRoot(), "configs");
            Directory.CreateDirectory(configsDirectory);
            string destination = Path.Combine(configsDirectory, fileName);
            if (!string.Equals(
                    Path.GetFullPath(sourcePath),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destination, overwrite: true);
            }

            _subscriptions!.Select(fileName);
            _mainWindow?.AppendLog(L("log.subscription.local_imported", fileName));
            RefreshConfigList();
            if (FirstRunGuidePolicy.ShouldAnnounceImportSuccess(configCountBeforeImport, corePresent))
            {
                ShowMessage(
                    L("guide.import_success.title"),
                    L("guide.import_success.message", fileName),
                    isError: false);
            }

            await ApplySelectedConfigAsync();
        }
        catch (Exception error)
        {
            string message = L("log.subscription.local_import_failed", error.Message);
            _mainWindow?.AppendLog(message);
            ShowMessage(L("guide.import_failed.title"), message, isError: true);
        }
    }

    private async void OnConfigActivateRequested(string fileName)
    {
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;   // 远程目标配置只读
        }

        try
        {
            _subscriptions!.Select(fileName);
            _mainWindow?.AppendLog(L("log.config.switched", fileName));
            RefreshConfigList();
            await ApplySelectedConfigAsync();
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.config.switch_failed", error.Message));
        }
    }

    private void OnConfigRemoveRequested(string fileName)
    {
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;   // 远程目标配置只读
        }

        try
        {
            _subscriptions!.Remove(fileName);
            _mainWindow?.AppendLog(L("log.config.removed", fileName));
            RefreshConfigList();
            if (_subscriptions.GetSelectedConfigPath() == null)
            {
                _ = ApplySelectedConfigAsync();   // 删除的是当前配置 → 回默认配置热重载
            }
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.config.remove_failed", error.Message));
        }
    }

    // ---- 订阅更新 / 复制代理命令 / 配置监控（M4-3d 功能核对补齐） ----

    /// <summary>更新单个远程订阅（源：订阅行刷新——重下载覆盖；选中配置则热重载）。</summary>
    private async void OnSubscriptionRefreshRequested(string fileName)
    {
        SubscriptionInfo? info = _subscriptions!.List().FirstOrDefault(item => item.FileName == fileName);
        if (info is null)
        {
            _mainWindow?.AppendLog(L("log.subscription.not_found", fileName));
            return;
        }

        await RefreshSubscriptionCoreAsync(info);
    }

    private async void OnSubscriptionRefreshAllRequested()
    {
        foreach (SubscriptionInfo info in _subscriptions!.List().ToList())
        {
            await RefreshSubscriptionCoreAsync(info);
        }
    }

    private async Task RefreshSubscriptionCoreAsync(SubscriptionInfo info)
    {
        if (_remoteStore?.IsRemoteTarget == true)
        {
            return;   // 远程目标订阅更新只读
        }

        try
        {
            string version = _poller?.Version is { Length: > 0 } current
                ? current
                : DefaultMihomoVersionForUserAgent;
            await _subscriptions!.AddAsync(new Uri(info.Url), $"clash.meta/{version}");
            _mainWindow?.AppendLog(L("log.subscription.updated", info.FileName));

            // 更新的是当前选中配置 → 热重载生效（源：更新后配置即时生效语义）
            if (_subscriptions.GetSelectedFileName() == info.FileName)
            {
                await ApplySelectedConfigAsync();
            }
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.subscription.update_failed", info.FileName, error.Message));
        }
    }

    /// <summary>
    /// 复制终端代理命令（源：copyLocalProxyCommand）。shell 决定格式：
    /// bash=export（代理页按钮）、cmd=set、powershell=$env:（托盘二级菜单）。
    /// </summary>
    private async Task OnCopyProxyCommandRequestedAsync(string shell)
    {
        try
        {
            ConfigSnapshot config = await _apiClient!.RequestAsync<ConfigSnapshot>(MihomoEndpoint.GetConfigs());
            int mixedPort = config.MixedPort ?? 0;
            if (mixedPort <= 0)
            {
                _mainWindow?.AppendLog(L("log.proxy.no_mixed_port_for_command"));
                return;
            }

            string endpoint = $"http://127.0.0.1:{mixedPort}";
            string command = BuildProxyCommand(shell, endpoint);
            await Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Clipboard.SetText(command);
                    _tray?.ShowTransientMessage(L("log.tray.command_copied"));
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // 剪贴板被占用时静默
                }
            });
            _mainWindow?.AppendLog(L("log.proxy.command_copied"));
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.proxy.copy_failed", error.Message));
        }
    }

    /// <summary>按 shell 生成代理命令（bash=export / cmd=set / powershell=$env:）。</summary>
    private static string BuildProxyCommand(string shell, string endpoint) => shell switch
    {
        "cmd" => $"set http_proxy={endpoint} && set https_proxy={endpoint} && set all_proxy={endpoint}",
        "powershell" => $"$env:http_proxy=\"{endpoint}\"; $env:https_proxy=\"{endpoint}\"; $env:all_proxy=\"{endpoint}\"",
        _ => $"export http_proxy={endpoint} https_proxy={endpoint} all_proxy={endpoint}",
    };

    /// <summary>
    /// 配置目录监控（源：AppViewModel+ConfigMonitoring——目录变化 350ms 防抖后，
    /// 若涉及当前选中配置且内核在跑则热重载）。
    /// </summary>
    private void SetupConfigMonitoring()
    {
        string configsDirectory = Path.Combine(ClashBarXPaths.AppDataRoot(), "configs");
        Directory.CreateDirectory(configsDirectory);
        var watcher = new FileSystemWatcher(configsDirectory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnConfigFileTouched;
        watcher.Created += OnConfigFileTouched;
        _configWatcher = watcher;

        // 根目录手工 config.yaml 同样监控
        string rootConfig = ClashBarXPaths.DefaultConfigPath();
        if (File.Exists(rootConfig))
        {
            var rootWatcher = new FileSystemWatcher(Path.GetDirectoryName(rootConfig)!)
            {
                Filter = Path.GetFileName(rootConfig),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            rootWatcher.Changed += OnConfigFileTouched;
            _configRootWatcher = rootWatcher;
        }
    }

    private void OnConfigFileTouched(object sender, FileSystemEventArgs e)
    {
        lock (_configDebounceGate)
        {
            _pendingConfigChangePath = e.FullPath;
            _configDebounceTimer?.Dispose();
            _configDebounceTimer = new System.Threading.Timer(
                _ => Dispatcher.BeginInvoke(() => _ = ApplyPendingConfigChangeAsync()),
                null,
                TimeSpan.FromMilliseconds(350),
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ApplyPendingConfigChangeAsync()
    {
        string? changedPath;
        lock (_configDebounceGate)
        {
            changedPath = _pendingConfigChangePath;
            _pendingConfigChangePath = null;
        }

        // 源：AppViewModel+ConfigMonitoring 的 guard !isRemoteTarget
        if (changedPath is null || _processManager?.IsRunning != true || _remoteStore?.IsRemoteTarget == true)
        {
            return;
        }

        string selectedPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
        // 仅当前生效配置的变化触发重载（源：监控只关心选中配置）
        if (!string.Equals(Path.GetFullPath(changedPath), Path.GetFullPath(selectedPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _mainWindow?.AppendLog(L("log.config.hot_reloaded", Path.GetFileName(changedPath)));
        await ApplySelectedConfigAsync();
    }

    // ---- 手动内核生命周期（源：MenuBarHeaderView 的启动/停止/重启动作） ----

    /// <summary>是否存在可启动的运行配置（选中配置或根目录手工 config.yaml）。</summary>
    private bool HasRunnableConfig() => FirstRunGuidePolicy.HasRunnableConfig(
        _subscriptions?.GetSelectedConfigPath(), ClashBarXPaths.DefaultConfigPath());

    /// <summary>
    /// 没有可启动的运行配置：提示先导入订阅，可一键前往订阅设置
    /// （源：log.start.no_config + reportCoreStartFailure 的启动失败告警）。
    /// </summary>
    private void ShowNoConfigPrompt()
    {
        _mainWindow?.AppendLog(L("log.start.no_config"));
        System.Windows.MessageBoxResult choice = System.Windows.MessageBox.Show(
            _mainWindow,
            L("guide.no_config.message"),
            L("guide.no_config.title"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information);
        if (choice == System.Windows.MessageBoxResult.Yes)
        {
            _mainWindow?.FocusSubscriptionUrlBox();
        }
    }

    private async Task StartCoreManualAsync()
    {
        if (_processManager?.IsRunning == true)
        {
            _mainWindow?.AppendLog(L("log.core.already_running"));
            return;
        }

        // 首次使用引导链 ②：还没有可启动的配置（还没导入订阅）→ 引导去导入
        if (!HasRunnableConfig())
        {
            ShowNoConfigPrompt();
            return;
        }

        string configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
        try
        {
            (string launchPath, string? workingDirectory) = ResolveCoreLaunchConfig(configPath);
            await _processManager!.StartAsync(launchPath, workingDirectory, DefaultController);
            _mainWindow?.AppendLog(L("log.core.started"));
        }
        catch (MihomoBinaryResolutionException)
        {
            _mainWindow?.AppendLog(L("log.core.binary_missing", ClashBarXPaths.CoreDirectory()));
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.core.start_failed", error.Message));
        }
        finally
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
    }

    private async Task StopCoreManualAsync()
    {
        if (_processManager?.IsRunning != true)
        {
            _mainWindow?.AppendLog(L("log.core.not_running"));
            return;
        }

        try
        {
            await _processManager.StopAsync();
            _mainWindow?.AppendLog(L("log.core.stopped"));
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.core.stop_failed", error.Message));
        }
        finally
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
    }

    private async Task RestartCoreManualAsync()
    {
        if (!HasRunnableConfig())
        {
            ShowNoConfigPrompt();
            return;
        }

        string configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
        try
        {
            (string launchPath, string? workingDirectory) = ResolveCoreLaunchConfig(configPath);
            await _processManager!.RestartAsync(launchPath, workingDirectory, DefaultController);
            _mainWindow?.AppendLog(L("log.core.restarted"));
        }
        catch (MihomoBinaryResolutionException)
        {
            _mainWindow?.AppendLog(L("log.core.restart_binary_missing", ClashBarXPaths.CoreDirectory()));
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.core.restart_failed", error.Message));
        }
        finally
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
    }

    /// <summary>
    /// 应用订阅选中配置（或默认配置）：内核运行中优先 PUT /configs 热重载
    /// （源：submitCoreLaunchConfig——覆写时推 payload 全文、否则推 path；连接不断、
    /// 进程不重启）；未运行时启动；热重载失败记错误日志（内核继续用旧配置，源语义）。
    /// </summary>
    private async Task ApplySelectedConfigAsync()
    {
        string configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
        try
        {
            (string launchPath, string? workingDirectory) = ResolveCoreLaunchConfig(configPath);
            if (_processManager is { IsRunning: true })
            {
                bool overrideEnabled = _settingsStore.Load().RuleOverrideEnabled;
                if (overrideEnabled)
                {
                    string payload = await File.ReadAllTextAsync(launchPath);
                    await _apiClient!.RequestNoResponseAsync(
                        MihomoEndpoint.PutConfigs(force: false, path: launchPath, payload: payload));
                    _mainWindow?.AppendLog(L("log.config.hot_reloaded_override", Path.GetFileName(launchPath)));
                }
                else
                {
                    await _apiClient!.RequestNoResponseAsync(
                        MihomoEndpoint.PutConfigs(force: false, path: launchPath, payload: null));
                    _mainWindow?.AppendLog(L("log.config.hot_reloaded_plain", Path.GetFileName(launchPath)));
                }

                return;
            }

            await _processManager!.StartAsync(launchPath, workingDirectory, DefaultController);
        }
        catch (MihomoBinaryResolutionException)
        {
            // 无内核时保持 no-core 引导提示，不重复刷
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog(L("log.core.restart_failed", error.Message));
        }
        finally
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
    }

    private string _trayCoreStatus = ClashBarX.Core.L10n.CoreL10n.T("tray.core_status.stopped");
    private string _trayTraffic = string.Empty;

    /// <summary>最近一次已知的内核版本（内核停止后 API 不可查，缓存以保证状态栏版本号常显）。</summary>
    private string? _lastKnownCoreVersion;

    /// <summary>托盘 tooltip 状态化（内核状态 + 实时速率；源：状态栏双行速率语义的 Windows 对应物）。</summary>
    private void UpdateTrayToolTip() =>
        _tray?.SetToolTip(_trayTraffic.Length > 0
            ? $"ClashBarX · {_trayCoreStatus} · {_trayTraffic}"
            : $"ClashBarX · {_trayCoreStatus}");

    /// <summary>托盘状态图标与主窗状态行的统一刷新（源：macOS 菜单栏 run/sleep 状态语义）。</summary>
    private void RefreshPresentationState()
    {
        bool coreRunning = _processManager?.IsRunning == true;
        ApiHealth apiHealth = _poller?.Status ?? ApiHealth.Unknown;
        if (_poller?.Version is { Length: > 0 } version)
        {
            _lastKnownCoreVersion = version;
        }

        // 代理页空态：未运行时显示「启动内核后…」并清掉陈旧面板（源：stopCore 的 clearProxyPresentation）
        _proxyPage?.ApplyCoreRunning(coreRunning);
        if (!coreRunning)
        {
            _proxyProviders?.ClearProviders();
        }

        // TUN 开启时用 TUN 品牌图（源：StatusItemContentView 的 tunBrandStatusIconImage 分支）
        _tray?.SetKind(coreRunning
            ? apiHealth == ApiHealth.Healthy
                ? _tunSection?.IsTunEnabled == true ? TrayIconKind.Tun : TrayIconKind.Running
                : TrayIconKind.Logo
            : TrayIconKind.Sleeping);
        // 托盘内核操作组互斥（定稿：与主窗互斥按钮同语义）
        _tray?.SetCoreMenuState(coreRunning);

        if (_mainWindow == null)
        {
            return;
        }

        // 头部状态点（绿=运行+API 正常 / 黄=运行但 API 未就绪 / 灰=未运行）与互斥内核按钮
        _mainWindow.SetCoreRunning(coreRunning);
        _mainWindow.SetCoreStatusDot(!coreRunning
            ? "#B0B7C0"
            : apiHealth == ApiHealth.Healthy ? "#34A853" : "#B45309");

        // 设置页关于卡内核块（版本 + 运行状态；源：kernel status 行）
        if (_settingsPage is not null)
        {
            _settingsPage.KernelVersionText = _lastKnownCoreVersion is { Length: > 0 } kernelVersion
                ? kernelVersion
                : "—";
            _settingsPage.KernelStatusText = coreRunning
                ? _processManager?.RunningProcessId is { } pid
                    ? L("tray.core_status.running_healthy", pid, string.Empty)
                    : null
                : L("tray.core_status.stopped");
        }

        if (!coreRunning)
        {
            // 版本号始终展示（源：状态栏 stopped_with_version 语义）
            _trayCoreStatus = _lastKnownCoreVersion is { Length: > 0 } stoppedVersion
                ? L("tray.core_status.stopped_with_version", stoppedVersion)
                : L("tray.core_status.stopped");
            _mainWindow.SetCoreStatus(_trayCoreStatus);
            _mainWindow.SetStatusRate("—");
            _mainWindow.SetStatusMisc(
                ClashBarX.Core.L10n.CoreL10n.T("proxy.traffic.connections", 0));
        }
        else if (apiHealth == ApiHealth.Healthy)
        {
            string healthyVersion = _poller?.Version is string v ? $" · {v}" : string.Empty;
            _trayCoreStatus = L("tray.core_status.running_healthy", _processManager!.RunningProcessId, healthyVersion);
            _mainWindow.SetCoreStatus(_trayCoreStatus);
        }
        else if (apiHealth == ApiHealth.Degraded)
        {
            _trayCoreStatus = L("tray.core_status.running_degraded", _processManager!.RunningProcessId);
            _mainWindow.SetCoreStatus(_trayCoreStatus);
        }
        else
        {
            _trayCoreStatus = L("tray.core_status.running_waiting", _processManager!.RunningProcessId);
            _mainWindow.SetCoreStatus(_trayCoreStatus);
        }

        UpdateTrayToolTip();
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
