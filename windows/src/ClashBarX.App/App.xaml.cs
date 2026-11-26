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
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Overrides;
using ClashBarX.Core.Proxies;
using ClashBarX.Core.ProxyGuard;
using ClashBarX.Core.Rules;
using ClashBarX.Core.Runtime;
using ClashBarX.Core.Streams;
using ClashBarX.Core.Subscriptions;
using ClashBarX.Core.SystemProxy;

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
    private RegistryWatcher? _proxyWatcher;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private ProxyPageViewModel? _proxyPage;
    private ProxyBoardService? _proxyBoard;
    private ConnectionsPageViewModel? _connectionsPage;
    private ConnectionStatisticsService? _connectionStatistics;
    private bool _connectionsProcessMatchingEnabled;
    private RulesBoardService? _rulesBoard;
    private RulesPageViewModel? _rulesPage;
    private RuleOverrideService? _ruleOverride;
    private RuleOverrideViewModel? _ruleOverridePage;

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

        // CLI 触发（源：--proxy-guard——已有实例时通知其立即探测后本进程退出
        //（ProxyGuardCLI 的 exit(0) 语义）；本实例为首个实例时随启动触发）
        _cliProxyGuardTrigger = Environment.GetCommandLineArgs()
            .Contains(ProxyGuardService.CliTriggerArgument, StringComparer.OrdinalIgnoreCase);
        if (_cliProxyGuardTrigger && TryTriggerRunningGuardInstance())
        {
            Shutdown();   // 已通知运行中实例，不再启动第二份主窗/托盘/内核（避免抢占 9090 控制端口）
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
        _mainWindow.SystemProxyToggled += OnSystemProxyToggled;
        _mainWindow.SubscriptionImportRequested += OnSubscriptionImportRequested;
        _mainWindow.ConfigActivateRequested += OnConfigActivateRequested;
        _mainWindow.ConfigRemoveRequested += OnConfigRemoveRequested;

        _tray = new TrayIconService();
        _tray.ShowMainWindowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitFromTray;
        _tray.SystemProxyToggled += OnSystemProxyToggled;

        // 启动即显示主窗（可见反馈）；关闭主窗 = 隐藏到托盘，退出走托盘菜单
        _mainWindow.Show();
        RefreshConfigList();

        _ = StartCoreAtStartupAsync();
        StartApiPolling();
        SetupSystemProxy();
        SetupProxyPage();
        SetupRulesPage();
        SetupConnectionsPage();
        SetupLogsPage();
        SetupProxyGuard();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常退出路径也会先在此终结内核，避免任何路径漏杀
        _proxyRefreshTimer?.Dispose();
        _rulesRefreshTimer?.Dispose();
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
            _mainWindow?.AppendLog(
                $"[startup] 请将 mihomo.exe 放入 {ClashBarXPaths.CoreDirectory()}，"
                + $"并导入订阅或在 {ClashBarXPaths.DefaultConfigPath()} 准备运行配置后重启应用。");
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

    // ---- 系统代理（源：AppViewModel+SystemProxy.swift 的意图/守护语义） ----

    private void SetupSystemProxy()
    {
        var controller = new WindowsSystemProxyController();
        _systemProxy = new SystemProxyService(controller);
        _systemProxy.Logged += message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message));
        _systemProxy.EnabledChanged += enabled => Dispatcher.BeginInvoke(() =>
        {
            _mainWindow?.SetSystemProxyChecked(enabled);
            _tray?.SetSystemProxyChecked(enabled);
        });

        _proxyWatcher = new RegistryWatcher(
            WindowsSystemProxyController.InternetSettingsRegistryPath,
            () => Dispatcher.BeginInvoke(() => _systemProxy?.NotifyRegistryChanged()))
        {
            Logged = message => Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog(message)),
        };
        _proxyWatcher.Start();

        // 初始观测同步（外部工具可能已开/关代理）
        SystemProxySnapshot snapshot = _systemProxy.RefreshState();
        _mainWindow?.SetSystemProxyChecked(snapshot.Enabled);
        _tray?.SetSystemProxyChecked(snapshot.Enabled);

        // 启动恢复开启意图（源：startup repair 语义）：等待 API 就绪后拉取混合端口
        if (_settingsStore.Load().SystemProxyEnabled)
        {
            _ = EnableSystemProxyFromCoreConfigAsync(reason: "startup restore");
        }
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
            _settingsStore.Save(new AppSettings(SystemProxyEnabled: false));
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
                _mainWindow?.AppendLog($"[system proxy] {reason}: 内核未开启混合端口，无法设置代理");
                Dispatcher.BeginInvoke(() =>
                {
                    _mainWindow?.SetSystemProxyChecked(false);
                    _tray?.SetSystemProxyChecked(false);
                });
                _settingsStore.Save(new AppSettings(SystemProxyEnabled: false));
                return;
            }

            _systemProxy?.Enable(new SystemProxyTarget("127.0.0.1", mixedPort, SystemProxyDefaults.DefaultBypassList));
            _settingsStore.Save(new AppSettings(SystemProxyEnabled: true));
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
        _mainWindow!.DataContext = _proxyPage;

        // traffic WS 流（M1 的 StreamCoordinator：重连去抖复用）
        _streams = new StreamCoordinator
        {
            ShouldReconnect = () => _processManager?.IsRunning == true,
            OnDisconnect = (key, message) =>
                Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[stream] {key} 断开：{message}")),
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
                            _proxyPage?.UpdateTraffic(snapshot.Up, snapshot.Down));
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
                    Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[traffic] 处理失败：{error.Message}"));
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
            _lastProxyRefreshError = null;   // 成功即清除指纹，下次失败可再记录
        }
        catch (Exception error)
        {
            // 连续失败按指纹去重记录（避免每 4 秒刷屏）
            string fingerprint = error.GetType().FullName + ":" + error.Message;
            if (_lastProxyRefreshError != fingerprint)
            {
                _lastProxyRefreshError = fingerprint;
                _mainWindow?.AppendLog($"[proxy] 面板刷新失败：{error.Message}");
            }
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
            _mainWindow?.AppendLog($"[override] 打开目录失败：{error.Message}");
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
            _mainWindow?.AppendLog(
                $"[override] 运行时配置已生成：{Path.GetFileName(runtimePath)}（前置 {counts.Prepend} · 追加 {counts.Append} · 规则集 {counts.RuleProviders}）");
        });
        return (runtimePath, Path.GetDirectoryName(Path.GetFullPath(configPath)));
    }

    /// <summary>运行中应用覆写（源：applyRuleOverridesIfRunning——PUT /configs 推 payload 全文）。</summary>
    private async Task ApplyRuleOverridesAsync()
    {
        if (!_ruleOverridePage?.IsEnabled == true)
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
                _mainWindow?.AppendLog($"[override] 已应用到运行中的内核（{Path.GetFileName(runtimePath)}）");
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Dispatcher.BeginInvoke(() =>
            {
                _ruleOverridePage?.MarkFailed(error.Message);
                _mainWindow?.AppendLog($"[override] 应用失败：{error.Message}");
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
                _mainWindow?.AppendLog($"[rules] 面板刷新失败：{error.Message}");
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
                                _mainWindow?.AppendLog($"[connections] 开启进程识别失败：{error.Message}"));
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
                    await Dispatcher.BeginInvoke(() => _connectionsPage?.IngestSnapshot(update));
                }
                catch (JsonException)
                {
                    // 噪声/半包 payload 丢弃（normalize 已滤常见噪声）
                }
                catch (Exception error)
                {
                    // 解码侧意外异常编组到 UI 线程记录，避免在线程池直调 UI 控件后循环触发重连
                    Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[connections] 处理失败：{error.Message}"));
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
                    Dispatcher.BeginInvoke(() => _mainWindow?.AppendLog($"[logs] 处理失败：{error.Message}"));
                }
            });
    }

    // ---- 代理守护（M3-3：源 AppViewModel+ProxyGuard.swift；CLI 触发的进程间通知
    //      选型为命名 EventWaitHandle——对齐 macOS 的 DistributedNotificationCenter，
    //      语义为「通知运行中实例立即探测；无实例时随应用启动触发」） ----

    /// <summary>守护触发的跨进程命名事件名（对应 macOS 的 com.clashbarx.proxyguard.trigger）。</summary>
    private const string GuardTriggerEventName = "ClashBarX.ProxyGuard.Trigger";

    private void SetupProxyGuard()
    {
        AppSettings settings = _settingsStore.Load();
        _guardIntervalSeconds = Math.Max(ProxyGuardService.MinimumIntervalSeconds, settings.ProxyGuardIntervalSeconds);

        _proxyGuard = new ProxyGuardService(
            _apiClient!,
            ProbeViaCoreProxyAsync,
            () => _guardIntervalSeconds,
            canProbe: () => _processManager?.IsRunning == true && _poller?.Status == ApiHealth.Healthy);
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

    private async void OnSubscriptionImportRequested(string urlText)
    {
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url))
        {
            _mainWindow?.AppendLog("[subscription] 地址无效，需要完整的 http(s) 链接");
            return;
        }

        _mainWindow?.SetImportBusy(true);
        try
        {
            // UA 携带 clash.meta/当前内核版本（源：remoteSubscriptionUserAgent）
            string version = _poller?.Version is string current && current.Length > 0
                ? current
                : DefaultMihomoVersionForUserAgent;
            SubscriptionInfo info = await _subscriptions!.AddAsync(url, $"clash.meta/{version}");

            _subscriptions.Select(info.FileName);
            _mainWindow?.AppendLog($"[subscription] 已导入并启用 {info.FileName}");
            RefreshConfigList();
            await RestartCoreWithSelectedAsync();
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog($"[subscription] 导入失败：{error.Message}");
        }
        finally
        {
            _mainWindow?.SetImportBusy(false);
        }
    }

    private async void OnConfigActivateRequested(string fileName)
    {
        try
        {
            _subscriptions!.Select(fileName);
            _mainWindow?.AppendLog($"[config] 已切换 {fileName}");
            RefreshConfigList();
            await RestartCoreWithSelectedAsync();
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog($"[config] 切换失败：{error.Message}");
        }
    }

    private void OnConfigRemoveRequested(string fileName)
    {
        try
        {
            _subscriptions!.Remove(fileName);
            _mainWindow?.AppendLog($"[config] 已删除 {fileName}");
            RefreshConfigList();
            if (_subscriptions.GetSelectedConfigPath() == null)
            {
                _ = RestartCoreWithSelectedAsync();   // 删除的是当前配置 → 回默认配置重启
            }
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog($"[config] 删除失败：{error.Message}");
        }
    }

    /// <summary>以订阅选中配置（或默认配置）重启内核。</summary>
    private async Task RestartCoreWithSelectedAsync()
    {
        MihomoProcessManager? manager = _processManager;
        if (manager == null)
        {
            return;
        }

        string configPath = _subscriptions?.GetSelectedConfigPath() ?? ClashBarXPaths.DefaultConfigPath();
        try
        {
            (string launchPath, string? workingDirectory) = ResolveCoreLaunchConfig(configPath);
            await manager.RestartAsync(launchPath, workingDirectory, DefaultController);
        }
        catch (MihomoBinaryResolutionException)
        {
            // 无内核时保持 no-core 引导提示，不重复刷
        }
        catch (Exception error)
        {
            _mainWindow?.AppendLog($"[core] 重启失败：{error.Message}");
        }
        finally
        {
            Dispatcher.BeginInvoke(RefreshPresentationState);
        }
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
