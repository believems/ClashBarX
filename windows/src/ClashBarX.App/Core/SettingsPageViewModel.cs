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
        new EnumOption<AppAppearanceMode>("跟随系统", AppAppearanceMode.System),
        new EnumOption<AppAppearanceMode>("浅色", AppAppearanceMode.Light),
        new EnumOption<AppAppearanceMode>("深色", AppAppearanceMode.Dark),
    };

    /// <summary>核心设置子模型（allow-lan/ipv6/tcp-concurrent/日志等级/端口；经 PATCH /configs）。</summary>
    public CoreSettingsViewModel CoreSettings { get; } = new();

    /// <summary>维护动作委托（App 层注入 MaintenanceService 调用；返回状态文案）。</summary>
    public Func<Task<string>>? UpgradeGeoRequested { get; set; }

    public Func<Task<string>>? UpgradeCoreRequested { get; set; }

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

    public SettingsPageViewModel()
    {
        UpgradeGeoCommand = new RelayCommand(_ => RunMaintenanceAsync(UpgradeGeoRequested));
        UpgradeCoreCommand = new RelayCommand(_ => RunMaintenanceAsync(UpgradeCoreRequested));
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
            MaintenanceStatusLevel = message.StartsWith("失败", StringComparison.Ordinal) ? "error" : "ok";
        }
        catch (Exception error)
        {
            MaintenanceStatusText = $"失败：{error.Message}";
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
