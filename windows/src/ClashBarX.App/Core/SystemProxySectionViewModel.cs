using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.SystemProxy;

namespace ClashBarX.App.Core;

/// <summary>
/// 网络页系统代理区块模型（源：SystemTabView 的绕过列表区块 + ProxyTabView 快捷开关）：
/// 开关镜像（状态由 App 推送，切换上抛）+ 绕过列表草稿（增删/恢复默认/保存）。
/// </summary>
public sealed class SystemProxySectionViewModel : INotifyPropertyChanged
{
    /// <summary>默认绕过列表（源：AppViewModel+SystemProxy.swift 的默认 exceptions）。</summary>
    public static readonly IReadOnlyList<string> DefaultBypassList = new[]
    {
        "::1", "*.local", "<local>", "localhost",
        "127.0.0.1", "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12",
    };

    private readonly Func<Task> _saveExceptions;

    private bool _isEnabled;
    private bool _isSyncing;
    private string _newException = string.Empty;
    private bool _hasPendingChanges;

    public SystemProxySectionViewModel(Func<Task> saveExceptions)
    {
        _saveExceptions = saveExceptions;
        RemoveExceptionCommand = new RelayCommand(parameter =>
        {
            if (parameter is BypassEntryViewModel entry)
            {
                RemoveException(entry);
            }

            return Task.CompletedTask;
        });
        AddExceptionCommand = new RelayCommand(_ =>
        {
            AddException();
            return Task.CompletedTask;
        });
        SaveExceptionsCommand = new RelayCommand(_ => SaveExceptionsAsync());
        ResetExceptionsCommand = new RelayCommand(_ =>
        {
            ResetToDefaults();
            return Task.CompletedTask;
        });
    }

    /// <summary>开关变化（参数为新状态）——App 层执行开/关并推送回镜像。</summary>
    public event Action<bool>? ToggleRequested;

    /// <summary>开关镜像（程序化同步不回环触发；源：isProxySyncing 语义）。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                ToggleRequested?.Invoke(value);
            }
        }
    }

    /// <summary>切换进行中（禁用开关防连点；源：isProxySyncing）。</summary>
    public bool IsSyncing
    {
        get => _isSyncing;
        set => SetProperty(ref _isSyncing, value);
    }

    public ObservableCollection<BypassEntryViewModel> Exceptions { get; } =
        new(DefaultBypassList.Select(value => new BypassEntryViewModel(value)));

    /// <summary>新增条目输入框。</summary>
    public string NewException
    {
        get => _newException;
        set
        {
            SetProperty(ref _newException, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddException)));
        }
    }

    public bool CanAddException => _newException.Trim().Length > 0;

    /// <summary>草稿有未保存修改。</summary>
    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set => SetProperty(ref _hasPendingChanges, value);
    }

    public ICommand RemoveExceptionCommand { get; }

    public ICommand AddExceptionCommand { get; }

    public ICommand SaveExceptionsCommand { get; }

    public ICommand ResetExceptionsCommand { get; }

    /// <summary>程序化同步开关态（不触发 ToggleRequested；调用方负责 UI 线程）。</summary>
    public void SetEnabled(bool enabled) => SetProperty(ref _isEnabled, enabled);

    /// <summary>载入持久化的绕过列表（null = 默认）。</summary>
    public void LoadExceptions(IReadOnlyList<string>? saved)
    {
        Exceptions.Clear();
        foreach (string value in saved ?? DefaultBypassList)
        {
            Exceptions.Add(new BypassEntryViewModel(value));
        }

        HasPendingChanges = false;
    }

    /// <summary>新增条目（去重忽略大小写；源：appendSystemProxyException）。</summary>
    public void AddException()
    {
        string value = _newException.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (!NormalizedExceptions().Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            Exceptions.Add(new BypassEntryViewModel(value));
            HasPendingChanges = true;
        }

        NewException = string.Empty;
    }

    internal void RemoveException(BypassEntryViewModel entry)
    {
        if (Exceptions.Remove(entry))
        {
            HasPendingChanges = true;
        }
    }

    /// <summary>归一化后的当前列表（保存与重写系统代理用）。</summary>
    public IReadOnlyList<string> NormalizedExceptions() =>
        Exceptions.Select(entry => entry.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>恢复默认列表（不落盘，待保存；源：恢复默认按钮语义）。</summary>
    public void ResetToDefaults()
    {
        LoadExceptions(null);
        HasPendingChanges = true;
    }

    private async Task SaveExceptionsAsync()
    {
        await _saveExceptions();
        HasPendingChanges = false;
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

/// <summary>绕过条目（行内可编辑；string 不支持双向绑定到集合项，故包装）。</summary>
public sealed class BypassEntryViewModel : INotifyPropertyChanged
{
    private string _value;

    public BypassEntryViewModel(string value) => _value = value;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
