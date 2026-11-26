using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.Overrides;

namespace ClashBarX.App.Core;

/// <summary>
/// 规则覆写区模型（源：RuleOverrideStore + OverrideTabView 交互）：
/// 三文件区（同一时间只展开一个）、草稿与脏标记、启用开关（持久化）、
/// 状态行（applied/failed/empty/pending）、立即应用与打开目录。
/// </summary>
public sealed class RuleOverrideViewModel : INotifyPropertyChanged
{
    private readonly RuleOverrideService _service;
    private readonly Func<Task> _applyNow;
    private readonly Func<bool> _canApply;
    private readonly Action<string> _log;
    private readonly Action _openFolder;

    private bool _isEnabled;
    private RuleOverrideFile? _expandedFile;
    private string _currentDraft = string.Empty;
    private bool _isApplying;
    private string? _statusText;
    private string _statusLevel = string.Empty;   // ok / error / info
    private RuleOverrideCounts _counts = new(0, 0, 0);

    private readonly Dictionary<RuleOverrideFile, string> _drafts = new();
    private readonly Dictionary<RuleOverrideFile, string> _savedTexts = new();

    public RuleOverrideViewModel(
        RuleOverrideService service,
        Func<Task> applyNow,
        Func<bool> canApply,
        Action<string> log,
        Action openFolder)
    {
        _service = service;
        _applyNow = applyNow;
        _canApply = canApply;
        _log = log;
        _openFolder = openFolder;
        ToggleFileCommand = new RelayCommand(parameter =>
        {
            if (parameter is RuleOverrideFile file)
            {
                ToggleFile(file);
            }

            return Task.CompletedTask;
        });
        SaveCommand = new RelayCommand(_ => SaveCurrentDraftAsync());
        RevertCommand = new RelayCommand(_ =>
        {
            RevertCurrentDraft();
            return Task.CompletedTask;
        });
        ApplyNowCommand = new RelayCommand(_ => ApplyNowCoreAsync());
        OpenFolderCommand = new RelayCommand(_ =>
        {
            _openFolder();
            return Task.CompletedTask;
        });
    }

    /// <summary>启用开关（变化上抛；持久化由 App 层承担；源：clashbar.rule_override.enabled）。</summary>
    public event Action<bool>? EnabledToggled;

    /// <summary>三文件有效计数变化（刷新文件区行）。</summary>
    public event Action? CountsChanged;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                if (!value)
                {
                    StatusText = null;   // 关闭收起状态行（源：区块折叠）
                }

                EnabledToggled?.Invoke(value);
            }
        }
    }

    /// <summary>当前展开的文件（null = 全折叠；同一时间只展开一个，源：expandedFile）。</summary>
    public RuleOverrideFile? ExpandedFile
    {
        get => _expandedFile;
        private set
        {
            if (SetProperty(ref _expandedFile, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPrependExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAppendExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProvidersExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasExpandedFile)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            }
        }
    }

    /// <summary>三文件区行的展开态（驱动行视觉）。</summary>
    public bool IsPrependExpanded => ExpandedFile == RuleOverrideFile.Prepend;

    public bool IsAppendExpanded => ExpandedFile == RuleOverrideFile.Append;

    public bool IsProvidersExpanded => ExpandedFile == RuleOverrideFile.RuleProviders;

    /// <summary>编辑器显隐（任一文件展开时显示）。</summary>
    public bool HasExpandedFile => ExpandedFile is not null;

    /// <summary>展开文件的草稿文本（编辑器双向绑定；草稿变更驱动脏标记）。</summary>
    public string CurrentDraft
    {
        get => _currentDraft;
        set
        {
            if (SetProperty(ref _currentDraft, value) && _expandedFile is { } file)
            {
                _drafts[file] = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            }
        }
    }

    public bool IsDirty => _expandedFile is { } file && _drafts.GetValueOrDefault(file) != _savedTexts.GetValueOrDefault(file);

    /// <summary>状态行文案（null = 隐藏；源：applied/failed/empty/pending 四态）。</summary>
    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatus)));
            }
        }
    }

    /// <summary>状态行等级（ok/error/info），XAML DataTrigger 用。</summary>
    public string StatusLevel
    {
        get => _statusLevel;
        private set => SetProperty(ref _statusLevel, value);
    }

    public bool HasStatus => StatusText is { Length: > 0 };

    /// <summary>关闭时的说明行（源：未启用 hint）。</summary>
    public string DisabledHint => "开启后把 overrides/ 下的三份文件拼进运行时配置，原配置不会被修改";

    public RuleOverrideCounts Counts
    {
        get => _counts;
        private set => SetProperty(ref _counts, value);
    }

    public bool IsApplying
    {
        get => _isApplying;
        private set => SetProperty(ref _isApplying, value);
    }

    public ICommand ToggleFileCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RevertCommand { get; }

    public ICommand ApplyNowCommand { get; }

    public ICommand OpenFolderCommand { get; }

    /// <summary>初始化：读磁盘文件载入草稿与计数（不改开关态；调用方负责 UI 线程）。</summary>
    public void ReloadFromFilesystem()
    {
        foreach (RuleOverrideFile file in Enum.GetValues<RuleOverrideFile>())
        {
            string text = SafeEditableText(file);
            _savedTexts[file] = text;
            if (!_drafts.ContainsKey(file))
            {
                _drafts[file] = text;
            }
        }

        RefreshCounts();
        if (_expandedFile is { } expanded)
        {
            CurrentDraft = _drafts[expanded];
        }
    }

    /// <summary>恢复开关态（不触发 EnabledToggled；源：Restore 语义）。</summary>
    public void Restore(bool enabled)
    {
        _isEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
    }

    /// <summary>文件区行的展示数据。</summary>
    public (string Title, string Glyph, string CountText) FileSection(RuleOverrideFile file) => file switch
    {
        RuleOverrideFile.Prepend => ("前置规则（插在订阅 rules 最前）", "↑", $"{Counts.Prepend} 条"),
        RuleOverrideFile.Append => ("追加规则（插在 MATCH/FINAL 之前）", "↓", $"{Counts.Append} 条"),
        RuleOverrideFile.RuleProviders => ("规则集（同名以覆写为准）", "≡", $"{Counts.RuleProviders} 个"),
        _ => (string.Empty, string.Empty, string.Empty),
    };

    /// <summary>已应用到配置（源：markRuleOverrideApplied——含三计数日志）。</summary>
    public void MarkApplied(string configName, RuleOverrideCounts counts)
    {
        StatusText = $"已应用到 {configName}（前置 {counts.Prepend} · 追加 {counts.Append} · 规则集 {counts.RuleProviders}）";
        StatusLevel = "ok";
        RefreshCounts();
    }

    /// <summary>应用失败（源：markRuleOverrideFailed）。</summary>
    public void MarkFailed(string message)
    {
        StatusText = $"应用失败：{message}";
        StatusLevel = "error";
    }

    /// <summary>开着但三文件无有效内容（源：empty 状态）。</summary>
    public void MarkEmpty()
    {
        StatusText = "未配置覆写规则";
        StatusLevel = "info";
    }

    /// <summary>待下次启动或重载生效（源：pending 状态）。</summary>
    public void MarkPending()
    {
        StatusText = "下次启动或重载配置时生效";
        StatusLevel = "info";
    }

    internal void ToggleFile(RuleOverrideFile file)
    {
        if (_expandedFile == file)
        {
            ExpandedFile = null;
            return;
        }

        ExpandedFile = file;
        CurrentDraft = _drafts.GetValueOrDefault(file, string.Empty);
    }

    internal void RevertCurrentDraft()
    {
        if (_expandedFile is not { } file)
        {
            return;
        }

        CurrentDraft = _savedTexts.GetValueOrDefault(file, string.Empty);
        _drafts[file] = CurrentDraft;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    private async Task SaveCurrentDraftAsync()
    {
        if (_expandedFile is not { } file || !IsDirty)
        {
            return;
        }

        try
        {
            _service.Write(CurrentDraft, file);
            _savedTexts[file] = CurrentDraft;
            RefreshCounts();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            _log($"[override] 已保存 {file.FileName()}");
            await _applyNow();   // 保存后触发应用（源：saveRuleOverrideDraft 的 apply 联动）
        }
        catch (Exception error)
        {
            MarkFailed(error.Message);
            _log($"[override] 保存失败：{error.Message}");
        }
    }

    private async Task ApplyNowCoreAsync()
    {
        if (IsApplying || !_canApply())
        {
            return;
        }

        IsApplying = true;
        try
        {
            await _applyNow();
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void RefreshCounts()
    {
        try
        {
            Counts = _service.LoadContent().Counts;
        }
        catch (Exception error)
        {
            _log($"[override] 计数刷新失败：{error.Message}");
        }

        CountsChanged?.Invoke();
    }

    private string SafeEditableText(RuleOverrideFile file)
    {
        try
        {
            return _service.EditableText(file);
        }
        catch (Exception error)
        {
            _log($"[override] 读取 {file.FileName()} 失败：{error.Message}");
            return string.Empty;
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
