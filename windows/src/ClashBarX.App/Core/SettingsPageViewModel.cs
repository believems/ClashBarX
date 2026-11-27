using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClashBarX.App.Core;

/// <summary>外观模式（源：AppAppearanceMode——system/light/dark）。</summary>
public enum AppAppearanceMode
{
    System,
    Light,
    Dark,
}

/// <summary>界面语言（源：AppLanguage——zh-Hans/en）。</summary>
public enum AppLanguage
{
    ChineseSimplified,
    English,
}

/// <summary>
/// 设置页模型（源：AppViewModel+Settings.swift / SystemTabView 基础设置区）：
/// 外观三态、语言中英、开机自启（HKCU Run）、流量概览显隐。变化上抛由 App 层
/// 应用主题/切换语言字典/写注册表/持久化。
/// </summary>
public sealed class SettingsPageViewModel : INotifyPropertyChanged
{
    private AppAppearanceMode _appearanceMode;
    private AppLanguage _language;
    private bool _launchAtLogin;
    private bool _launchAtLoginFailed;
    private bool _showTrafficOverview = true;

    /// <summary>外观变化（App 层应用主题字典）。</summary>
    public event Action<AppAppearanceMode>? AppearanceChanged;

    /// <summary>语言变化（App 层切换字符串字典）。</summary>
    public event Action<AppLanguage>? LanguageChanged;

    /// <summary>自启写入委托（App 层注入：写 HKCU Run 并持久化）。</summary>
    public Func<bool, Task>? LaunchAtLoginRequested { get; set; }

    /// <summary>流量概览显隐变化（代理页摘要绑定）。</summary>
    public event Action<bool>? TrafficOverviewChanged;

    public AppAppearanceMode AppearanceMode
    {
        get => _appearanceMode;
        set
        {
            if (SetProperty(ref _appearanceMode, value))
            {
                AppearanceChanged?.Invoke(value);
            }
        }
    }

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
            {
                LanguageChanged?.Invoke(value);
            }
        }
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (!SetProperty(ref _launchAtLogin, value))
            {
                return;
            }

            ApplyLaunchAtLoginAsync(value);
        }
    }

    /// <summary>自启写入失败（行内错误提示）。</summary>
    public bool LaunchAtLoginFailed
    {
        get => _launchAtLoginFailed;
        private set => SetProperty(ref _launchAtLoginFailed, value);
    }

    public bool ShowTrafficOverview
    {
        get => _showTrafficOverview;
        set
        {
            if (SetProperty(ref _showTrafficOverview, value))
            {
                TrafficOverviewChanged?.Invoke(value);
            }
        }
    }

    public IReadOnlyList<EnumOption<AppAppearanceMode>> AppearanceOptions { get; } = new[]
    {
        new EnumOption<AppAppearanceMode>(ClashBarX.Core.L10n.CoreL10n.T("settings.appearance.system"), AppAppearanceMode.System),
        new EnumOption<AppAppearanceMode>(ClashBarX.Core.L10n.CoreL10n.T("settings.appearance.light"), AppAppearanceMode.Light),
        new EnumOption<AppAppearanceMode>(ClashBarX.Core.L10n.CoreL10n.T("settings.appearance.dark"), AppAppearanceMode.Dark),
    };

    /// <summary>核心设置子模型（allow-lan/ipv6/tcp-concurrent/日志等级/端口；经 PATCH /configs）。</summary>
    public CoreSettingsViewModel CoreSettings { get; } = new();

    /// <summary>维护动作委托（App 层注入 MaintenanceService 调用；返回状态文案）。</summary>
    public Func<Task<string>>? UpgradeGeoRequested { get; set; }

    public Func<Task<string>>? UpgradeCoreRequested { get; set; }

    public Func<Task<string>>? FlushFakeIPRequested { get; set; }

    public Func<Task<string>>? FlushDNSRequested { get; set; }

    // ---- 关于与更新检查（源：MenuBarCommonViews footer + availableAppUpdate） ----

    /// <summary>检查更新委托（App 层注入 fetch + resolve；返回可用更新，null = 已是最新）。</summary>
    public Func<Task<ClashBarX.Core.Updates.AppReleaseInfo?>>? CheckUpdateRequested { get; set; }

    /// <summary>用户请求打开发布页（有更新打开 release 页，否则打开 releases 列表）。</summary>
    public event Action<string>? OpenReleasePageRequested;

    private string _currentVersionText = "1.0.0";
    private ClashBarX.Core.Updates.AppReleaseInfo? _availableUpdate;
    private bool _isCheckingUpdate;
    private string? _updateStatusText;
    private bool _updateCheckFailed;

    /// <summary>当前应用版本（源：currentAppVersionText；装配层注入程序集版本）。</summary>
    public string CurrentVersionText
    {
        get => _currentVersionText;
        set => SetProperty(ref _currentVersionText, value);
    }

    /// <summary>可用更新（null = 无；源：availableAppReleaseInfo）。</summary>
    public ClashBarX.Core.Updates.AppReleaseInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (SetProperty(ref _availableUpdate, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvailableUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableUpdateVersionText)));
            }
        }
    }

    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public string AvailableUpdateVersionText => AvailableUpdate?.DisplayVersion ?? string.Empty;

    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        private set => SetProperty(ref _isCheckingUpdate, value);
    }

    /// <summary>更新检查反馈行（null = 隐藏）。</summary>
    public string? UpdateStatusText
    {
        get => _updateStatusText;
        private set
        {
            if (SetProperty(ref _updateStatusText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUpdateStatus)));
            }
        }
    }

    public bool HasUpdateStatus => UpdateStatusText is { Length: > 0 };

    /// <summary>检查失败（错误着色；源：fetch 失败静默——手动检查给出反馈）。</summary>
    public bool UpdateCheckFailed
    {
        get => _updateCheckFailed;
        private set => SetProperty(ref _updateCheckFailed, value);
    }

    public System.Windows.Input.ICommand CheckUpdateCommand { get; }

    /// <summary>发布页入口（源：appReleaseIndexURL / update.releaseURL）。</summary>
    public const string ReleasesIndexUrl = "https://github.com/believems/ClashBarX/releases";

    public void OpenReleasePage()
    {
        string url = AvailableUpdate?.ReleaseUrl ?? ReleasesIndexUrl;
        OpenReleasePageRequested?.Invoke(url);
    }

    /// <summary>启动自动检查结果回填（源：refreshLatestAppRelease——同结果不重复刷）。</summary>
    public void ApplyLatestRelease(ClashBarX.Core.Updates.AppReleaseInfo? latest)
    {
        ClashBarX.Core.Updates.AppReleaseInfo? update = ClashBarX.Core.Updates.AppReleaseService
            .ResolveAvailableUpdate(latest, CurrentVersionText);
        if (update is not null)
        {
            AvailableUpdate = update;
            UpdateStatusText = ClashBarX.Core.L10n.CoreL10n.T("update.available", update.DisplayVersion);
            UpdateCheckFailed = false;
        }
    }

    // ---- 内核块（定稿：关于卡右半——mihomo 版本 / 检查更新 / 发现新版后才显示升级按钮 + 发布页） ----

    /// <summary>内核更新检查委托（App 层注入 mihomo releases fetch + resolve；null = 已是最新）。</summary>
    public Func<Task<ClashBarX.Core.Updates.AppReleaseInfo?>>? CheckKernelUpdateRequested { get; set; }

    /// <summary>mihomo 发布页（源：内核块「发布页 ↗」固定链接）。</summary>
    public const string KernelReleasesUrl = "https://github.com/MetaCubeX/mihomo/releases";

    private string _kernelVersionText = "—";
    private string? _kernelStatusText;
    private ClashBarX.Core.Updates.AppReleaseInfo? _availableKernelUpdate;
    private bool _isCheckingKernelUpdate;
    private string? _kernelUpdateStatusText;
    private bool _kernelUpdateCheckFailed;

    /// <summary>内核版本（App 层喂 API version；未知时「—」）。</summary>
    public string KernelVersionText
    {
        get => _kernelVersionText;
        set => SetProperty(ref _kernelVersionText, value);
    }

    /// <summary>内核运行状态（运行中 pid / 未运行；App 层随 RefreshPresentationState 刷新）。</summary>
    public string? KernelStatusText
    {
        get => _kernelStatusText;
        set
        {
            if (SetProperty(ref _kernelStatusText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasKernelStatus)));
            }
        }
    }

    public bool HasKernelStatus => KernelStatusText is { Length: > 0 };

    /// <summary>内核可用更新（null = 无；发现后才显示「升级内核」按钮）。</summary>
    public ClashBarX.Core.Updates.AppReleaseInfo? AvailableKernelUpdate
    {
        get => _availableKernelUpdate;
        private set
        {
            if (SetProperty(ref _availableKernelUpdate, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasKernelUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KernelUpgradeText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KernelFoundText)));
            }
        }
    }

    public bool HasKernelUpdate => AvailableKernelUpdate is not null;

    /// <summary>升级按钮文案（「升级到 vX」；按钮隐藏时不用）。</summary>
    public string KernelUpgradeText => AvailableKernelUpdate is { } update
        ? ClashBarX.Core.L10n.CoreL10n.T("about.kernel_upgrade", update.DisplayVersion)
        : string.Empty;

    /// <summary>发现新版提示（「发现新版本 vX」）。</summary>
    public string KernelFoundText => AvailableKernelUpdate is { } update
        ? ClashBarX.Core.L10n.CoreL10n.T("about.kernel_found", update.DisplayVersion)
        : string.Empty;

    public bool IsCheckingKernelUpdate
    {
        get => _isCheckingKernelUpdate;
        private set => SetProperty(ref _isCheckingKernelUpdate, value);
    }

    /// <summary>内核更新检查反馈行（null = 隐藏）。</summary>
    public string? KernelUpdateStatusText
    {
        get => _kernelUpdateStatusText;
        set
        {
            if (SetProperty(ref _kernelUpdateStatusText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasKernelUpdateStatus)));
            }
        }
    }

    public bool HasKernelUpdateStatus => KernelUpdateStatusText is { Length: > 0 };

    public bool KernelUpdateCheckFailed
    {
        get => _kernelUpdateCheckFailed;
        private set => SetProperty(ref _kernelUpdateCheckFailed, value);
    }

    public System.Windows.Input.ICommand CheckKernelUpdateCommand { get; }

    public void OpenKernelReleasePage() => OpenReleasePageRequested?.Invoke(KernelReleasesUrl);

    private async Task RunKernelUpdateCheckAsync()
    {
        if (IsCheckingKernelUpdate || CheckKernelUpdateRequested is null)
        {
            return;
        }

        IsCheckingKernelUpdate = true;
        try
        {
            ClashBarX.Core.Updates.AppReleaseInfo? update = await CheckKernelUpdateRequested();
            AvailableKernelUpdate = update;
            KernelUpdateStatusText = update is null
                ? ClashBarX.Core.L10n.CoreL10n.T("update.up_to_date")
                : ClashBarX.Core.L10n.CoreL10n.T("about.kernel_found", update.DisplayVersion);
            KernelUpdateCheckFailed = false;
        }
        catch (Exception error)
        {
            AvailableKernelUpdate = null;
            KernelUpdateStatusText = ClashBarX.Core.L10n.CoreL10n.T("update.failed", error.Message);
            KernelUpdateCheckFailed = true;
        }
        finally
        {
            IsCheckingKernelUpdate = false;
        }
    }

    private async Task RunUpdateCheckAsync()
    {
        if (IsCheckingUpdate || CheckUpdateRequested is null)
        {
            return;
        }

        IsCheckingUpdate = true;
        try
        {
            ClashBarX.Core.Updates.AppReleaseInfo? update = await CheckUpdateRequested();
            AvailableUpdate = update;
            UpdateCheckFailed = false;
            UpdateStatusText = update is null
                ? ClashBarX.Core.L10n.CoreL10n.T("update.latest")
                : ClashBarX.Core.L10n.CoreL10n.T("update.available", update.DisplayVersion);
        }
        catch (Exception error)
        {
            UpdateCheckFailed = true;
            UpdateStatusText = ClashBarX.Core.L10n.CoreL10n.T("update.failed", error.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private bool _isMaintaining;
    private string? _maintenanceStatusText;
    private string _maintenanceStatusLevel = "info";

    public bool IsMaintaining
    {
        get => _isMaintaining;
        private set => SetProperty(ref _isMaintaining, value);
    }

    /// <summary>维护结果反馈行（null = 隐藏）。</summary>
    public string? MaintenanceStatusText
    {
        get => _maintenanceStatusText;
        private set
        {
            if (SetProperty(ref _maintenanceStatusText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMaintenanceStatus)));
            }
        }
    }

    public string MaintenanceStatusLevel
    {
        get => _maintenanceStatusLevel;
        private set => SetProperty(ref _maintenanceStatusLevel, value);
    }

    public bool HasMaintenanceStatus => MaintenanceStatusText is { Length: > 0 };

    public System.Windows.Input.ICommand UpgradeGeoCommand { get; }

    public System.Windows.Input.ICommand UpgradeCoreCommand { get; }

    public System.Windows.Input.ICommand FlushFakeIPCommand { get; }

    public System.Windows.Input.ICommand FlushDNSCommand { get; }

    public SettingsPageViewModel()
    {
        UpgradeGeoCommand = new RelayCommand(_ => RunMaintenanceAsync(UpgradeGeoRequested));
        UpgradeCoreCommand = new RelayCommand(_ => RunMaintenanceAsync(UpgradeCoreRequested));
        FlushFakeIPCommand = new RelayCommand(_ => RunMaintenanceAsync(FlushFakeIPRequested));
        FlushDNSCommand = new RelayCommand(_ => RunMaintenanceAsync(FlushDNSRequested));
        CheckUpdateCommand = new RelayCommand(_ => RunUpdateCheckAsync());
        CheckKernelUpdateCommand = new RelayCommand(_ => RunKernelUpdateCheckAsync());
    }

    /// <summary>执行维护动作并反馈结果（进行中禁用按钮；源：维护区交互）。</summary>
    private async Task RunMaintenanceAsync(Func<Task<string>>? action)
    {
        if (IsMaintaining || action is null)
        {
            return;
        }

        IsMaintaining = true;
        try
        {
            string message = await action();
            MaintenanceStatusText = message;
            MaintenanceStatusLevel = message.StartsWith(ClashBarX.Core.L10n.CoreL10n.T("maintenance.failed").Split(':')[0], StringComparison.Ordinal) ? "error" : "ok";
        }
        catch (Exception error)
        {
            MaintenanceStatusText = ClashBarX.Core.L10n.CoreL10n.T("maintenance.failed", error.Message);
            MaintenanceStatusLevel = "error";
        }
        finally
        {
            IsMaintaining = false;
        }
    }

    public IReadOnlyList<EnumOption<AppLanguage>> LanguageOptions { get; } = new[]
    {
        new EnumOption<AppLanguage>("简体中文", AppLanguage.ChineseSimplified),
        new EnumOption<AppLanguage>("English", AppLanguage.English),
    };

    private async Task ApplyLaunchAtLoginAsync(bool enabled)
    {
        LaunchAtLoginFailed = false;
        try
        {
            if (LaunchAtLoginRequested is { } handler)
            {
                await handler(enabled);
            }
        }
        catch (Exception)
        {
            // 写注册表失败：回滚开关并置错误提示（源：applyLaunchAtLogin 的错误横幅）
            LaunchAtLoginFailed = true;
            SetProperty(ref _launchAtLogin, !enabled, nameof(LaunchAtLogin));
        }
    }

    /// <summary>恢复持久化态（不触发事件；源：Restore 语义）。</summary>
    public void Restore(
        AppAppearanceMode appearance,
        AppLanguage language,
        bool launchAtLogin,
        bool showTrafficOverview)
    {
        _appearanceMode = appearance;
        _language = language;
        _launchAtLogin = launchAtLogin;
        _showTrafficOverview = showTrafficOverview;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppearanceMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LaunchAtLogin)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTrafficOverview)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
