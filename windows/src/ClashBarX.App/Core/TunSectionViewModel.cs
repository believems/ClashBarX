using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashBarX.Core.Tun;

namespace ClashBarX.App.Core;

/// <summary>stack 选项（mips 门槛不满足时禁选并附升级提示文案）。</summary>
public sealed class StackOptionViewModel
{
    public StackOptionViewModel(string stack, bool isAvailable, string mipsMinimumVersion)
    {
        Stack = stack;
        IsAvailable = isAvailable;
        MipsMinimumVersion = mipsMinimumVersion;
    }

    public string Stack { get; }

    /// <summary>mips 门槛不满足时 false（源：菜单项 disabled）。</summary>
    public bool IsAvailable { get; }

    public string MipsMinimumVersion { get; }

    /// <summary>显示文案（源：mips 不满足时 "mips (需内核 x.y.z)" 的对应物）。</summary>
    public string DisplayText => IsAvailable
        ? Stack
        : $"{Stack} · 需内核 {MipsMinimumVersion}+";
}

/// <summary>
/// TUN 快捷区 VM（源：ProxyTabView.tunModeQuickToggleRow/tunStackMenu + AppViewModel 的
/// isTunEnabled/isTunSyncing/tunStack 状态）：开关与 stack 选择上抛事件，
/// 实际开关动作（PATCH/验证/提权）由 App 装配层承担；运行态经 ApplySnapshot 回填。
/// </summary>
public sealed class TunSectionViewModel : INotifyPropertyChanged
{
    private readonly Func<bool> _canToggle;
    private bool _isTunEnabled;
    private bool _isTunSyncing;
    private string? _selectedStack;
    private bool _isMipsSupported;

    public TunSectionViewModel(Func<bool> canToggle)
    {
        _canToggle = canToggle;
        StackOptions = new ObservableCollection<StackOptionViewModel>(BuildStackOptions());
    }

    /// <summary>用户切换开关（参数为新状态；App 层完成 PATCH/验证/提权后回填实际态）。</summary>
    public event Action<bool>? ToggleRequested;

    /// <summary>用户选择 stack（mips 门槛不满足时仍上抛，App 层按源语义拒绝并警告）。</summary>
    public event Action<string>? StackSelected;

    public ObservableCollection<StackOptionViewModel> StackOptions { get; }

    /// <summary>同步中（禁用开关与 stack 选择；源：isTunSyncing）。</summary>
    public bool IsTunSyncing
    {
        get => _isTunSyncing;
        private set
        {
            if (SetProperty(ref _isTunSyncing, value))
            {
                Raise(nameof(IsTunToggleEnabled));
            }
        }
    }

    /// <summary>开关可用（源：isTunToggleEnabled = (运行中) 且非同步中）。</summary>
    public bool IsTunToggleEnabled => _canToggle() && !_isTunSyncing;

    public bool IsTunEnabled
    {
        get => _isTunEnabled;
        set
        {
            if (_isTunEnabled == value)
            {
                return;
            }

            _isTunEnabled = value;
            Raise(nameof(IsTunEnabled));
            ToggleRequested?.Invoke(value);
        }
    }

    /// <summary>当前 stack（显示用，实际生效值由运行态回填）。</summary>
    public string? SelectedStack
    {
        get => _selectedStack;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.Equals(_selectedStack, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedStack = value;
            Raise(nameof(SelectedStack));
            StackSelected?.Invoke(value);
        }
    }

    /// <summary>同步内核版本门槛（不满足时 mips 禁选；源：isMipsStackSupported）。</summary>
    public void SetMipsSupport(bool supported)
    {
        if (_isMipsSupported == supported)
        {
            return;
        }

        _isMipsSupported = supported;
        StackOptions.Clear();
        foreach (StackOptionViewModel option in BuildStackOptions())
        {
            StackOptions.Add(option);
        }
    }

    /// <summary>程序化回填运行态（不触发 Toggle/StackSelected 事件；源：fetchRuntimeConfigSnapshot 同步链）。</summary>
    public void ApplySnapshot(bool enabled, string? stack)
    {
        _isTunEnabled = enabled;
        if (!string.IsNullOrWhiteSpace(stack))
        {
            _selectedStack = stack.Trim();
        }

        Raise(nameof(IsTunEnabled));
        Raise(nameof(SelectedStack));
    }

    /// <summary>标记同步中（PATCH 期间防重入；完成后回 false）。</summary>
    public void SetSyncing(bool syncing) => IsTunSyncing = syncing;

    private List<StackOptionViewModel> BuildStackOptions() =>
        TunControlService.StackOptions
            .Select(stack => new StackOptionViewModel(
                stack,
                isAvailable: stack != "mips" || _isMipsSupported,
                TunControlService.MipsStackMinimumCoreVersion))
            .ToList();

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
