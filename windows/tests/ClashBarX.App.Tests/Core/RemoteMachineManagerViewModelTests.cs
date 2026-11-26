using ClashBarX.App.Core;
using ClashBarX.Core.Remote;

namespace ClashBarX.App.Tests.Core;

/// <summary>远程机器管理窗 VM 规格（源：RemoteMachineManagerView/EditorView 的表单与行集合语义）。</summary>
public class RemoteMachineManagerViewModelTests
{
    private static RemoteMachine Machine(string name = "demo", string host = "10.0.0.8", int port = 9091) =>
        new(Guid.NewGuid(), name, host, port, null, false);

    [Fact]
    public void BeginAdd_ResetsFormToDefaults()
    {
        var viewModel = new RemoteMachineManagerViewModel();

        viewModel.BeginAdd();

        Assert.True(viewModel.IsEditorVisible);
        Assert.Null(viewModel.EditingMachineId);
        Assert.Equal(string.Empty, viewModel.NameText);
        Assert.Equal(string.Empty, viewModel.HostText);
        Assert.Equal("9090", viewModel.PortText);
        Assert.Equal(string.Empty, viewModel.SecretText);
        Assert.False(viewModel.UseHttps);
        Assert.False(viewModel.IsFormValid);
    }

    [Fact]
    public void BeginEdit_PopulatesFormFromMachine()
    {
        var viewModel = new RemoteMachineManagerViewModel();
        RemoteMachine machine = new(Guid.NewGuid(), "demo", "10.0.0.8", 9091, "s3cret", true);

        viewModel.BeginEdit(machine);

        Assert.True(viewModel.IsEditorVisible);
        Assert.Equal(machine.Id, viewModel.EditingMachineId);
        Assert.Equal("demo", viewModel.NameText);
        Assert.Equal("10.0.0.8", viewModel.HostText);
        Assert.Equal("9091", viewModel.PortText);
        Assert.Equal("s3cret", viewModel.SecretText);
        Assert.True(viewModel.UseHttps);
        Assert.True(viewModel.IsFormValid);
    }

    [Theory]
    [InlineData("", "host", "9090", false)]        // name 空
    [InlineData("name", "", "9090", false)]        // host 空
    [InlineData("name", "host", "0", false)]       // 端口 0
    [InlineData("name", "host", "65536", false)]   // 端口超限
    [InlineData("name", "host", "abc", false)]     // 端口非数字
    [InlineData("name", "host", "9090", true)]
    [InlineData(" name ", "host", "1", true)]      // trim 后非空
    public void IsFormValid_EnforcesSourceRules(string name, string host, string port, bool expected)
    {
        var viewModel = new RemoteMachineManagerViewModel();

        viewModel.BeginAdd();
        viewModel.NameText = name;
        viewModel.HostText = host;
        viewModel.PortText = port;

        Assert.Equal(expected, viewModel.IsFormValid);
    }

    [Fact]
    public void ConnectionPreview_UsesPlaceholdersWhenEmpty()
    {
        // 源：connectionPreview——空 host 用 controller.example.com、空端口用 9090
        var viewModel = new RemoteMachineManagerViewModel();

        viewModel.BeginAdd();

        Assert.Equal("http://controller.example.com:9090", viewModel.ConnectionPreview);

        viewModel.UseHttps = true;
        Assert.Equal("https://controller.example.com:9090", viewModel.ConnectionPreview);
    }

    [Fact]
    public void Save_TrimsAndNormalizesSecret()
    {
        var viewModel = new RemoteMachineManagerViewModel();
        RemoteMachine? saved = null;
        viewModel.SaveRequested += machine => saved = machine;

        viewModel.BeginAdd();
        viewModel.NameText = "  office  ";
        viewModel.HostText = " 10.1.2.3 ";
        viewModel.PortText = "9095";
        viewModel.SecretText = "   ";

        viewModel.Save();

        Assert.NotNull(saved);
        Assert.Equal("office", saved!.Name);
        Assert.Equal("10.1.2.3", saved.Host);
        Assert.Equal(9095, saved.Port);
        Assert.Null(saved.Secret);   // 源：空 secret → nil
        Assert.False(viewModel.IsEditorVisible);
    }

    [Fact]
    public void Save_IgnoredWhenFormInvalid()
    {
        var viewModel = new RemoteMachineManagerViewModel();
        int saved = 0;
        viewModel.SaveRequested += _ => saved++;

        viewModel.BeginAdd();
        viewModel.Save();

        Assert.Equal(0, saved);
    }

    [Fact]
    public void Save_EditKeepsMachineId()
    {
        var viewModel = new RemoteMachineManagerViewModel();
        RemoteMachine machine = Machine();
        RemoteMachine? saved = null;
        viewModel.SaveRequested += item => saved = item;

        viewModel.BeginEdit(machine);
        viewModel.NameText = "renamed";
        viewModel.Save();

        Assert.NotNull(saved);
        Assert.Equal(machine.Id, saved!.Id);
        Assert.Equal("renamed", saved.Name);
    }

    [Fact]
    public void RebuildRows_BuildsRowsWithStatusAndActiveFlag()
    {
        var viewModel = new RemoteMachineManagerViewModel();
        RemoteMachine first = Machine("first");
        RemoteMachine second = Machine("second", host: "10.0.0.9");
        var machines = new[] { first, second };
        var statuses = new Dictionary<Guid, MachineConnectionStatus>
        {
            [first.Id] = MachineConnectionStatus.Connected("v1.19.0"),
            [second.Id] = MachineConnectionStatus.Failed("Timeout"),
        };

        viewModel.RebuildRows(machines, second.Id, id => statuses.GetValueOrDefault(id) ?? MachineConnectionStatus.Unknown);

        Assert.Equal(2, viewModel.Rows.Count);
        Assert.False(viewModel.Rows[0].IsActive);
        Assert.True(viewModel.Rows[1].IsActive);
        Assert.True(viewModel.Rows[0].IsConnected);
        Assert.Equal("v1.19.0", viewModel.Rows[0].StatusText);
        Assert.True(viewModel.Rows[1].IsFailed);
        Assert.Equal("Timeout", viewModel.Rows[1].StatusText);
        Assert.False(viewModel.Rows[1].CanSwitch);   // 失败不可切换（源：isSwitchEnabled）
        Assert.True(viewModel.Rows[0].CanSwitch);    // 已连接且非激活 → 可切换
    }

    [Fact]
    public void CanSwitch_RequiresConnectedAndNotActive()
    {
        var viewModel = new RemoteMachineManagerViewModel();
        RemoteMachine machine = Machine();
        viewModel.RebuildRows(
            new[] { machine }, null,
            _ => MachineConnectionStatus.Connected("v1"));

        Assert.True(viewModel.Rows[0].CanSwitch);

        viewModel.ApplyStatus(machine.Id, MachineConnectionStatus.Connected("v1"), machine.Id);
        Assert.False(viewModel.Rows[0].CanSwitch);   // 激活目标不可切换
    }
}
