using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.App.Core;

/// <summary>Provider 行（源：proxyProviderRow——名称/节点数/相对更新时间/到期/配额/动作）。</summary>
public sealed class ProviderRowViewModel : INotifyPropertyChanged
{
    private string _updatedText = string.Empty;
    private string _expireText = string.Empty;
    private bool _expiresSoon;
    private string? _quotaText;
    private double? _usedRatio;
    private bool _isUpdating;
    private int _nodeCount;

    public ProviderRowViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>节点数（源：proxies?.count ?? 0）。</summary>
    public int NodeCount
    {
        get => _nodeCount;
        private set => SetProperty(ref _nodeCount, value);
    }

    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    public string ExpireText
    {
        get => _expireText;
        private set => SetProperty(ref _expireText, value);
    }

    /// <summary>到期临近（<15 天警示色；源：expiresSoon）。</summary>
    public bool ExpiresSoon
    {
        get => _expiresSoon;
        private set => SetProperty(ref _expiresSoon, value);
    }

    /// <summary>配额文案（已用/总量；null = 无订阅信息隐藏）。</summary>
    public string? QuotaText
    {
        get => _quotaText;
        private set => SetProperty(ref _quotaText, value);
    }

    /// <summary>用量比例（0..1；null 隐藏；源：usedRatio）。</summary>
    public double? UsedRatio
    {
        get => _usedRatio;
        private set
        {
            if (SetProperty(ref _usedRatio, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QuotaLevel)));
            }
        }
    }

    /// <summary>配额水位（正常/warn ≥75%/critical ≥90%；源：比例条配色）。</summary>
    public string QuotaLevel => UsedRatio switch
    {
        >= 0.9 => "critical",
        >= 0.75 => "warn",
        _ => "normal",
    };

    /// <summary>该 provider 更新中（源：providerUpdating.contains）。</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        set => SetProperty(ref _isUpdating, value);
    }

    /// <summary>详情回填（refresh 后由装配层调用；now 用于相对时间与到期计算）。</summary>
    public void ApplyDetail(ProviderDetail? detail, DateTimeOffset now)
    {
        NodeCount = detail?.Proxies?.Count ?? 0;
        UpdatedText = ProviderFormatting.RelativeTime(detail?.UpdatedAt, now);
        ExpireText = ProviderFormatting.DaysUntilExpiryShort(detail?.SubscriptionInfo?.Expire, now);
        ExpiresSoon = ProviderFormatting.ExpiresSoon(detail?.SubscriptionInfo?.Expire, now);

        ProviderSubscriptionInfo? subscription = detail?.SubscriptionInfo;
        if (subscription?.Upload is { } upload && subscription.Download is { } download && subscription.Total is { } total)
        {
            long used = upload + download;
            QuotaText = $"{ProviderFormatting.BytesCompactNoSpace(used)} / {ProviderFormatting.BytesCompactNoSpace(total)}";
            UsedRatio = total > 0 ? Math.Clamp((double)used / total, 0, 1) : null;
        }
        else
        {
            QuotaText = null;
            UsedRatio = null;
        }
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

/// <summary>
/// 代理页 Provider 区 VM（源：proxyProvidersSection）：可折叠、更新全部、
/// 行内 refresh 与 healthcheck 动作上抛事件（API 调用由装配层承担）。
/// </summary>
public sealed class ProxyProvidersSectionViewModel : INotifyPropertyChanged
{
    private bool _isCollapsed;
    private bool _isRefreshingAll;

    /// <summary>用户请求更新全部 provider（源：refreshAllProxyProviders）。</summary>
    public event Action? UpdateAllRequested;

    /// <summary>用户请求更新单个 provider（参数为 provider 名）。</summary>
    public event Action<string>? UpdateProviderRequested;

    /// <summary>用户请求对单个 provider healthcheck。</summary>
    public event Action<string>? HealthcheckProviderRequested;

    public ObservableCollection<ProviderRowViewModel> Rows { get; } = new();

    public System.Windows.Input.ICommand UpdateAllCommand { get; }

    public System.Windows.Input.ICommand UpdateProviderCommand { get; }

    public System.Windows.Input.ICommand HealthcheckProviderCommand { get; }

    public ProxyProvidersSectionViewModel()
    {
        UpdateAllCommand = new RelayCommand(_ =>
        {
            RequestUpdateAll();
            return Task.CompletedTask;
        });
        UpdateProviderCommand = new RelayCommand(parameter =>
        {
            if (parameter is string providerName)
            {
                RequestUpdate(providerName);
            }

            return Task.CompletedTask;
        });
        HealthcheckProviderCommand = new RelayCommand(parameter =>
        {
            if (parameter is string providerName)
            {
                RequestHealthcheck(providerName);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>折叠（源：isProxyProvidersCollapsed，默认展开）。</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => SetProperty(ref _isCollapsed, value);
    }

    /// <summary>更新全部进行中（禁用顶部按钮；源：isProxyProvidersRefreshing）。</summary>
    public bool IsRefreshingAll
    {
        get => _isRefreshingAll;
        private set => SetProperty(ref _isRefreshingAll, value);
    }

    public void SetRefreshingAll(bool refreshing) => IsRefreshingAll = refreshing;

    /// <summary>整体重建（面板刷新后；provider 按名称排序——源：sortedProxyProviderNames）。</summary>
    public void ApplyProviders(
        IReadOnlyDictionary<string, ProviderDetail> providers,
        Func<string, ProviderDetail?>? detailOverride = null,
        DateTimeOffset? now = null)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.Now;
        Rows.Clear();
        foreach (string name in providers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ProviderRowViewModel(name);
            row.ApplyDetail(detailOverride?.Invoke(name) ?? providers.GetValueOrDefault(name), timestamp);
            Rows.Add(row);
        }

        Raise(nameof(HasProviders));
    }

    /// <summary>单 provider 详情回填（更新动作完成后刷新该行）。</summary>
    public void ApplyProviderDetail(string providerName, ProviderDetail? detail, DateTimeOffset? now = null)
    {
        ProviderRowViewModel? row = Rows.FirstOrDefault(item => item.Name == providerName);
        row?.ApplyDetail(detail, now ?? DateTimeOffset.Now);
    }

    /// <summary>内核停止后清空陈旧面板（源：stopCore 的 clearProxyPresentation）。</summary>
    public void ClearProviders()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        Rows.Clear();
        Raise(nameof(HasProviders));
    }

    public bool HasProviders => Rows.Count > 0;

    /// <summary>标记单个 provider 更新中（动作开始/结束）。</summary>
    public void SetProviderUpdating(string providerName, bool updating)
    {
        ProviderRowViewModel? row = Rows.FirstOrDefault(item => item.Name == providerName);
        if (row != null)
        {
            row.IsUpdating = updating;
        }
    }

    public void RequestUpdateAll() => UpdateAllRequested?.Invoke();

    public void RequestUpdate(string providerName) => UpdateProviderRequested?.Invoke(providerName);

    public void RequestHealthcheck(string providerName) => HealthcheckProviderRequested?.Invoke(providerName);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
