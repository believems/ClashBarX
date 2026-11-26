using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashBarX.Core.Remote;

namespace ClashBarX.App.Core;

/// <summary>管理窗机器行（源：RemoteMachineRowView——名称/地址/状态/当前目标与切换可用性）。</summary>
public sealed class RemoteMachineRowViewModel : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isChecking;
    private bool _isConnected;
    private bool _isFailed;
    private string _statusText = string.Empty;

    public RemoteMachineRowViewModel(RemoteMachine machine)
    {
        Machine = machine;
    }

    public RemoteMachine Machine { get; }

    public Guid Id => Machine.Id;

    public string Name => Machine.Name;

    public string DisplayAddress => Machine.DisplayAddress;

    /// <summary>当前激活目标（源：activeTargetID == machine.id）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        set => SetProperty(ref _isChecking, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        set => SetProperty(ref _isFailed, value);
    }

    /// <summary>状态摘要（连接成功显示版本，失败显示原因）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>可切换（源：isSwitchEnabled = status.isConnected && !isActive）。</summary>
    public bool CanSwitch => IsConnected && !IsActive;

    /// <summary>状态刷新后联动派生属性。</summary>
    public void ApplyStatus(MachineConnectionStatus status)
    {
        IsChecking = status.Kind == MachineConnectionKind.Checking;
        IsConnected = status.Kind == MachineConnectionKind.Connected;
        IsFailed = status.Kind == MachineConnectionKind.Failed;
        StatusText = status.Kind switch
        {
            MachineConnectionKind.Checking => ClashBarX.Core.L10n.CoreL10n.T("machine.status.checking"),
            MachineConnectionKind.Connected => status.Version is { Length: > 0 } ? status.Version : ClashBarX.Core.L10n.CoreL10n.T("machine.status.connected"),
            MachineConnectionKind.Failed => status.Reason ?? ClashBarX.Core.L10n.CoreL10n.T("machine.status.failed"),
            _ => ClashBarX.Core.L10n.CoreL10n.T("machine.status.unknown"),
        };
        Raise(nameof(CanSwitch));
    }

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

/// <summary>
/// 远程机器管理窗 VM（源：RemoteMachineManagerView + RemoteMachineEditorView）：
/// 行集合与编辑表单（新增/编辑两模式），增删改与切换目标上抛事件由 App 装配层承担。
/// </summary>
public sealed class RemoteMachineManagerViewModel : INotifyPropertyChanged
{
    /// <summary>端口缺省（源：编辑表单 port 初值 9090）。</summary>
    public const int DefaultPort = 9090;

    private string _nameText = string.Empty;
    private string _hostText = string.Empty;
    private string _portText = DefaultPort.ToString();
    private string _secretText = string.Empty;
    private bool _useHttps;
    private bool _isEditorVisible;
    private Guid? _editingMachineId;

    /// <summary>用户保存表单（参数为编辑结果；editingId 为 null 时新增）。</summary>
    public event Action<RemoteMachine>? SaveRequested;

    /// <summary>用户请求删除机器。</summary>
    public event Action<Guid>? DeleteRequested;

    /// <summary>用户请求切换目标（null = 本机；切换前由 App 层验证连通性）。</summary>
    public event Action<Guid?>? SwitchTargetRequested;

    public ObservableCollection<RemoteMachineRowViewModel> Rows { get; } = new();

    public bool IsEditorVisible
    {
        get => _isEditorVisible;
        private set => SetProperty(ref _isEditorVisible, value);
    }

    /// <summary>编辑中的机器 id（null = 新增模式）。</summary>
    public Guid? EditingMachineId
    {
        get => _editingMachineId;
        private set
        {
            if (SetProperty(ref _editingMachineId, value))
            {
                Raise(nameof(EditorTitle));
                Raise(nameof(IsEditingExisting));
            }
        }
    }

    /// <summary>编辑页标题（源：headerTitle——新增/编辑二态）。</summary>
    public string EditorTitle => ClashBarX.Core.L10n.CoreL10n.T(EditingMachineId is null ? "machine.editor.add_title" : "machine.editor.edit_title");

    /// <summary>正在编辑既有机器（密码框显示「留空保持不变」提示）。</summary>
    public bool IsEditingExisting => EditingMachineId is not null;

    public string NameText
    {
        get => _nameText;
        set
        {
            if (SetProperty(ref _nameText, value))
            {
                Raise(nameof(IsFormValid));
                Raise(nameof(ConnectionPreview));
            }
        }
    }

    public string HostText
    {
        get => _hostText;
        set
        {
            if (SetProperty(ref _hostText, value))
            {
                Raise(nameof(IsFormValid));
                Raise(nameof(ConnectionPreview));
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value))
            {
                Raise(nameof(IsFormValid));
                Raise(nameof(ConnectionPreview));
            }
        }
    }

    public string SecretText
    {
        get => _secretText;
        set => SetProperty(ref _secretText, value);
    }

    public bool UseHttps
    {
        get => _useHttps;
        set
        {
            if (SetProperty(ref _useHttps, value))
            {
                Raise(nameof(ConnectionPreview));
            }
        }
    }

    /// <summary>表单有效（源：isFormValid——name/host 非空 + port ∈ (0, 65535]）。</summary>
    public bool IsFormValid =>
        !string.IsNullOrWhiteSpace(NameText)
        && !string.IsNullOrWhiteSpace(HostText)
        && int.TryParse(PortText, out int port)
        && port > 0
        && port <= 65535;

    /// <summary>连接预览（源：connectionPreview——空 host 用占位域名）。</summary>
    public string ConnectionPreview
    {
        get
        {
            string host = string.IsNullOrWhiteSpace(HostText) ? "controller.example.com" : HostText.Trim();
            string port = string.IsNullOrWhiteSpace(PortText) ? DefaultPort.ToString() : PortText.Trim();
            return $"{(UseHttps ? "https" : "http")}://{host}:{port}";
        }
    }

    /// <summary>进入新增模式。</summary>
    public void BeginAdd()
    {
        EditingMachineId = null;
        NameText = string.Empty;
        HostText = string.Empty;
        PortText = DefaultPort.ToString();
        SecretText = string.Empty;
        UseHttps = false;
        IsEditorVisible = true;
    }

    /// <summary>进入编辑模式（源：onAppear 回填既有值）。</summary>
    public void BeginEdit(RemoteMachine machine)
    {
        EditingMachineId = machine.Id;
        NameText = machine.Name;
        HostText = machine.Host;
        PortText = machine.Port.ToString();
        SecretText = machine.Secret ?? string.Empty;
        UseHttps = machine.UseHttps;
        IsEditorVisible = true;
    }

    /// <summary>取消编辑（源：chevron.back 返回列表）。</summary>
    public void CancelEdit() => IsEditorVisible = false;

    /// <summary>保存（源：save——trim、端口兜底 9090、空 secret 置 null）。</summary>
    public void Save()
    {
        if (!IsFormValid)
        {
            return;
        }

        var machine = new RemoteMachine(
            EditingMachineId ?? Guid.NewGuid(),
            NameText.Trim(),
            HostText.Trim(),
            int.Parse(PortText),
            string.IsNullOrWhiteSpace(SecretText) ? null : SecretText.Trim(),
            UseHttps);
        SaveRequested?.Invoke(machine);
        IsEditorVisible = false;
    }

    public void RequestDelete(Guid id) => DeleteRequested?.Invoke(id);

    public void RequestSwitchTarget(Guid? machineId) => SwitchTargetRequested?.Invoke(machineId);

    /// <summary>整体重建行集合（列表/状态/目标变化后调用；UI 线程）。</summary>
    public void RebuildRows(
        IReadOnlyList<RemoteMachine> machines,
        Guid? activeMachineId,
        Func<Guid, MachineConnectionStatus> statusOf)
    {
        Rows.Clear();
        foreach (RemoteMachine machine in machines)
        {
            var row = new RemoteMachineRowViewModel(machine)
            {
                IsActive = activeMachineId == machine.Id,
            };
            row.ApplyStatus(statusOf(machine.Id));
            Rows.Add(row);
        }
    }

    /// <summary>单行状态刷新（探测事件后调用；机器不在列表时忽略）。</summary>
    public void ApplyStatus(Guid machineId, MachineConnectionStatus status, Guid? activeMachineId)
    {
        RemoteMachineRowViewModel? row = Rows.FirstOrDefault(item => item.Id == machineId);
        if (row == null)
        {
            return;
        }

        row.IsActive = activeMachineId == machineId;
        row.ApplyStatus(status);
    }

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
