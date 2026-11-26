using System.Windows;
using System.Windows.Controls;
using ClashBarX.App.Core;
using ClashBarX.Core.Remote;

namespace ClashBarX.App;

/// <summary>
/// 远程机器管理窗（源：RemoteMachineManagerView 的模态对应物）：列表（本机 + 远程行）
/// 与编辑表单两视图切换。窗口打开期间启动 5s 周期连通检查（源：onAppear/onDisappear），
/// 关闭即停。切换/增删改经 App 装配层回调执行（本窗只上抛意图）。
/// </summary>
public partial class RemoteMachineManagerWindow : Window
{
    /// <summary>用户请求切换目标（null = 本机）。</summary>
    public event Action<Guid?>? SwitchTargetRequested;

    /// <summary>用户保存机器（新增或编辑）。</summary>
    public event Action<RemoteMachine>? MachineSaved;

    /// <summary>用户删除机器。</summary>
    public event Action<Guid>? MachineDeleted;

    private readonly RemoteMachineManagerViewModel _viewModel;
    private readonly RemoteMachineStore _store;

    public RemoteMachineManagerWindow(
        RemoteMachineStore store,
        Func<string> localControllerDisplay)
    {
        InitializeComponent();
        _store = store;
        LocalControllerText.Text = localControllerDisplay();

        _viewModel = new RemoteMachineManagerViewModel();
        _viewModel.SaveRequested += machine => MachineSaved?.Invoke(machine);
        _viewModel.DeleteRequested += id => MachineDeleted?.Invoke(id);
        _viewModel.SwitchTargetRequested += machineId => SwitchTargetRequested?.Invoke(machineId);
        DataContext = _viewModel;

        RebuildRows();
        _store.MachinesChanged += OnStoreMachinesChanged;
        _store.StatusChanged += OnStoreStatusChanged;
        Closed += (_, _) =>
        {
            _store.MachinesChanged -= OnStoreMachinesChanged;
            _store.StatusChanged -= OnStoreStatusChanged;
            _store.StopPeriodicChecks();   // 源：onDisappear 停周期检查
        };

        // 源：onAppear 先全量一次再 5s 循环
        _store.StartPeriodicChecks();
    }

    private void RebuildRows() =>
        _viewModel.RebuildRows(_store.Machines, _store.ActiveMachineId, _store.StatusOf);

    private void OnStoreMachinesChanged() =>
        Dispatcher.BeginInvoke(RebuildRows);

    private void OnStoreStatusChanged(Guid machineId) =>
        Dispatcher.BeginInvoke(() => _viewModel.ApplyStatus(machineId, _store.StatusOf(machineId), _store.ActiveMachineId));

    private void OnAddMachineClick(object sender, RoutedEventArgs e) => _viewModel.BeginAdd();

    private void OnEditMachineClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteMachineRowViewModel row)
        {
            _viewModel.BeginEdit(row.Machine);
        }
    }

    private void OnDeleteMachineClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteMachineRowViewModel row)
        {
            _viewModel.RequestDelete(row.Id);
        }
    }

    private void OnSwitchToMachineClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteMachineRowViewModel row)
        {
            _viewModel.RequestSwitchTarget(row.Id);
        }
    }

    private void OnSwitchToLocalClick(object sender, RoutedEventArgs e) => _viewModel.RequestSwitchTarget(null);

    private void OnCancelEditClick(object sender, RoutedEventArgs e) => _viewModel.CancelEdit();

    private void OnSaveMachineClick(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.SecretText = box.Password;
        }
    }
}
