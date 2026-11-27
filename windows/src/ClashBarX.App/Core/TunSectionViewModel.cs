using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashBarX.Core.Tun;

namespace ClashBarX.App.Core;

/// <summary>stack 选项（mips 门槛不满足时禁选并附升级提示文案）。
/// IsAvailable 为可变通知属性——门槛变化时就地更新，不重建集合
/// （重建会触发 ComboBox 异步选中失效把 SelectedItem 回写 null，真机实测下拉永久空白）。</summary>
public sealed class StackOptionViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isAvailable;

    public StackOptionViewModel(string stack, bool isAvailable, string mipsMinimumVersion)
    {
        Stack = stack;
        _isAvailable = isAvailable;
        MipsMinimumVersion = mipsMinimumVersion;
    }

    public string Stack { get; }

    /// <summary>mips 门槛不满足时 false（源：菜单项 disabled）。</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable == value)
            {
                return;
            }

            _isAvailable = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsAvailable)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    public string MipsMinimumVersion { get; }

    /// <summary>显示文案（源：mips 不满足时 "mips (需内核 x.y.z)" 的对应物）。</summary>
    public string DisplayText => IsAvailable
        ? Stack
        : $"{Stack} · {ClashBarX.Core.L10n.CoreL10n.T("tun.mips_requires_upgrade", MipsMinimumVersion)}";

    /// <summary>UIA/无障碍与模板失效兜底显示（真机诊断：下拉项 Name 曾为类型全名）。</summary>
    public override string ToString() => DisplayText;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
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

    /// <summary>
    /// 选中项（对象级绑定——SelectedValue 字符串反查在 ItemsSource 填充前生效会
    /// 永久空选中，真机实测收起态空白；对象级 SelectedItem 无此坑）。
    /// </summary>
    public StackOptionViewModel? SelectedStackOption
    {
        get => _selectedStackOption;
        set
        {
            if (value is null || ReferenceEquals(_selectedStackOption, value))
            {
                return;
            }

            _selectedStackOption = value;
            Raise(nameof(SelectedStackOption));
            Raise(nameof(SelectedStack));
            StackSelected?.Invoke(value.Stack);
        }
    }

    private StackOptionViewModel? _selectedStackOption;

    /// <summary>当前 stack 名（显示用，实际生效值由运行态回填；由选中项派生）。</summary>
    public string? SelectedStack => _selectedStackOption?.Stack;

    /// <summary>同步内核版本门槛（不满足时 mips 禁选；源：isMipsStackSupported）。
    /// 就地更新 mips 项可用性——不重建集合，避免 ComboBox 选中被异步失效回写 null。</summary>
    public void SetMipsSupport(bool supported)
    {
        if (_isMipsSupported == supported)
        {
            return;
        }

        _isMipsSupported = supported;
        foreach (StackOptionViewModel option in StackOptions)
        {
            if (option.Stack == "mips")
            {
                option.IsAvailable = supported;
            }
        }
    }

    /// <summary>缺省 stack（源：原型下拉默认 mixed；持久化值为空时的兜底选中）。</summary>
    private const string DefaultStack = "mixed";

    /// <summary>程序化回填运行态（不触发 Toggle/StackSelected 事件；源：fetchRuntimeConfigSnapshot 同步链）。</summary>
    public void ApplySnapshot(bool enabled, string? stack)
    {
        bool enabledChanged = _isTunEnabled != enabled;
        string? currentStack = _selectedStackOption?.Stack;
        string? nextStack = string.IsNullOrWhiteSpace(stack) ? currentStack : stack.Trim();
        StackOptionViewModel? fallback =
            _selectedStackOption ?? StackOptions.FirstOrDefault(option => option.Stack == DefaultStack);
        StackOptionViewModel? nextOption = string.IsNullOrEmpty(nextStack)
            ? fallback
            : StackOptions.FirstOrDefault(option => option.Stack == nextStack)
              ?? fallback;   // 面板未就绪或值非法时兜底默认项，杜绝下拉空白
        bool stackChanged = !string.Equals(currentStack, nextOption?.Stack, StringComparison.Ordinal);
        _isTunEnabled = enabled;
        _selectedStackOption = nextOption;

        if (enabledChanged)
        {
            Raise(nameof(IsTunEnabled));
        }

        if (stackChanged)
        {
            Raise(nameof(SelectedStackOption));
            Raise(nameof(SelectedStack));
        }
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
