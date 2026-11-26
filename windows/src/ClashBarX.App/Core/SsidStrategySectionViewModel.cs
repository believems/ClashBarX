using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashBarX.Core.Ssid;

namespace ClashBarX.App.Core;

/// <summary>绑定行（SSID → 配置；移除命令注入）。</summary>
public sealed class SsidBindingRowViewModel
{
    public SsidBindingRowViewModel(SsidStrategyRule rule)
    {
        Ssid = rule.Ssid;
        ConfigFileName = rule.ConfigFileName;
    }

    public string Ssid { get; }

    public string ConfigFileName { get; }
}

/// <summary>
/// SSID 策略区 VM（源：ProxyTabView 的 ssidStrategy 快捷项 + SystemTab 的策略状态语义）：
/// 开关、当前 SSID、绑定行集合；命中应用与监听启停由 App 装配层承担。
/// </summary>
public sealed class SsidStrategySectionViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string? _currentSsid;

    /// <summary>用户切换自动切换开关（App 层启动/停止监听并持久化）。</summary>
    public event Action<bool>? EnabledToggled;

    /// <summary>用户请求把当前 SSID 绑定到当前选中配置（源：toggleCurrentSSIDBinding 的再绑定语义在 App 层）。</summary>
    public event Action? BindCurrentRequested;

    /// <summary>用户请求移除绑定。</summary>
    public event Action<string>? RemoveBindingRequested;

    public ObservableCollection<SsidBindingRowViewModel> Bindings { get; } = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            Raise(nameof(IsEnabled));
            EnabledToggled?.Invoke(value);
        }
    }

    /// <summary>当前 SSID（null = 未连接/不可读）。</summary>
    public string? CurrentSsid
    {
        get => _currentSsid;
        private set
        {
            if (string.Equals(_currentSsid, value, StringComparison.Ordinal))
            {
                return;
            }

            _currentSsid = value;
            Raise(nameof(CurrentSsid));
            Raise(nameof(CurrentSsidText));
        }
    }

    /// <summary>显示文本（源：ssidStrategyCurrentSSIDText——未连接时占位文案）。</summary>
    public string CurrentSsidText =>
        string.IsNullOrWhiteSpace(CurrentSsid) ? "未连接 Wi-Fi" : CurrentSsid!;

    /// <summary>程序化同步开关（不触发事件；持久化恢复用）。</summary>
    public void RestoreEnabled(bool enabled)
    {
        _isEnabled = enabled;
        Raise(nameof(IsEnabled));
    }

    /// <summary>SSID 变化回填（监听事件）。</summary>
    public void ApplyCurrentSsid(string? ssid) => CurrentSsid = ssid;

    /// <summary>整体重建绑定行（规则变化后）。</summary>
    public void ApplyBindings(IEnumerable<SsidStrategyRule> rules)
    {
        Bindings.Clear();
        foreach (SsidStrategyRule rule in SsidStrategyRule.Normalize(rules))
        {
            Bindings.Add(new SsidBindingRowViewModel(rule));
        }
    }

    public void RequestBindCurrent() => BindCurrentRequested?.Invoke();

    public void RequestRemove(string ssid) => RemoveBindingRequested?.Invoke(ssid);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
