using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.ProxyGuard;

namespace ClashBarX.App.Core;

/// <summary>
/// 代理守护设置区模型（源：SystemTabView.swift 守护区块 + SystemTabViewModel 的交互语义）。
/// 开启才展开「周期输入 + 立即检查 + 结果行」，关闭时显示说明文字；周期回写仅当
/// 解析为 30…86400 的整数，否则静默忽略；显示值 clamp 到下限 30。
/// </summary>
public sealed class ProxyGuardSettingsViewModel : INotifyPropertyChanged
{
    public const int MinimumIntervalSeconds = ProxyGuardService.MinimumIntervalSeconds;
    public const int MaximumIntervalSeconds = 86400;

    private readonly Func<Task> _checkNow;
    private readonly Func<bool> _canCheck;
    private bool _isGuardEnabled;
    private string _intervalText = "300";
    private bool _isProbing;
    private string? _outcomeText;
    private string _outcomeLevel = string.Empty;   // ok / warning / 空

    public ProxyGuardSettingsViewModel(Func<Task> checkNow, Func<bool> canCheck)
    {
        _checkNow = checkNow;
        _canCheck = canCheck;
        CheckNowCommand = new RelayCommand(_ => CheckNowAsync());
    }

    /// <summary>开关变化（参数为新状态）——App 层据此 Start/Stop 守护并持久化。</summary>
    public event Action<bool>? GuardToggled;

    /// <summary>守护周期被合法修改（秒）。App 层据此更新 intervalProvider。</summary>
    public event Action<int>? IntervalChanged;

    public bool IsGuardEnabled
    {
        get => _isGuardEnabled;
        set
        {
            if (SetProperty(ref _isGuardEnabled, value))
            {
                if (!value)
                {
                    OutcomeText = null;   // 关闭即清反馈行（源：区块收起）
                }

                GuardToggled?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// 周期输入框文本：合法（30…86400 整数）才接受并上抛；非法输入被拒绝且回弹显示
    /// （重新通知绑定读回生效值，源：Swift Binding get 重求值的还原语义）。
    /// </summary>
    public string IntervalText
    {
        get => _intervalText;
        set
        {
            if (int.TryParse(value.Trim(), out int seconds)
                && seconds is >= MinimumIntervalSeconds and <= MaximumIntervalSeconds)
            {
                if (SetProperty(ref _intervalText, seconds.ToString()))
                {
                    IntervalChanged?.Invoke(seconds);
                }

                return;
            }

            // 非法：丢弃输入并回弹（通知绑定重新读取当前生效值）
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IntervalText)));
        }
    }

    /// <summary>「立即检查」进行中（按钮文案切换「检查中…」）。</summary>
    public bool IsProbing
    {
        get => _isProbing;
        private set => SetProperty(ref _isProbing, value);
    }

    /// <summary>结果反馈行文案（null = 隐藏）。</summary>
    public string? OutcomeText
    {
        get => _outcomeText;
        private set
        {
            if (SetProperty(ref _outcomeText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOutcome)));
            }
        }
    }

    /// <summary>反馈行等级（ok=绿 / warning=黄），XAML DataTrigger 用。</summary>
    public string OutcomeLevel
    {
        get => _outcomeLevel;
        private set => SetProperty(ref _outcomeLevel, value);
    }

    public bool HasOutcome => OutcomeText is { Length: > 0 };

    /// <summary>关闭时的说明文字行（源：ui.settings.proxy_guard_disabled_hint）。</summary>
    public string DisabledHint => "开启后自动检测连通性，故障时切换组内节点并自愈订阅";

    public ICommand CheckNowCommand { get; }

    /// <summary>探测结果回填（源：proxyGuardOutcomeRow 的 reachable/failed 两态）。</summary>
    public void ApplyProbeState(ProxyGuardProbeState state)
    {
        if (state.Outcome == ProxyGuardProbeOutcome.Reachable)
        {
            OutcomeText = "网络连通正常";
            OutcomeLevel = "ok";
        }
        else
        {
            OutcomeText = $"探测失败（连续第 {state.ConsecutiveFailures} 次）";
            OutcomeLevel = "warning";
        }
    }

    private async Task CheckNowAsync()
    {
        if (IsProbing || !_canCheck())
        {
            return;
        }

        IsProbing = true;
        try
        {
            await _checkNow();
        }
        finally
        {
            IsProbing = false;
        }
    }

    /// <summary>从持久化设置恢复初始状态（不触发 GuardToggled 事件）。</summary>
    public void Restore(bool enabled, int intervalSeconds)
    {
        _isGuardEnabled = enabled;
        _intervalText = Math.Max(MinimumIntervalSeconds, intervalSeconds).ToString();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGuardEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IntervalText)));
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
