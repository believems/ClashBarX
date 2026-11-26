using System.Net;
using ClashBarX.Core.Remote;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Remote;

/// <summary>远程机器仓库规格测试（源：Stores/RemoteMachineStore.swift 逐行对照）。</summary>
public class RemoteMachineStoreTests : IDisposable
{
    private readonly string _storagePath;

    public RemoteMachineStoreTests()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), $"clashbarx-remote-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_storagePath);
        }
        catch (IOException)
        {
        }
    }

    private static RemoteMachine Machine(
        string name = "demo",
        string host = "192.168.1.10",
        int port = 9090,
        bool useHttps = false,
        string? secret = null) =>
        new(Guid.NewGuid(), name, host, port, secret, useHttps);

    private RemoteMachineStore CreateStore(StubHttpMessageHandler? handler = null) =>
        new(_storagePath, handler ?? new StubHttpMessageHandler());

    [Fact]
    public void AddMachine_PersistsAndRaises()
    {
        using RemoteMachineStore store = CreateStore();
        int raised = 0;
        store.MachinesChanged += () => raised++;

        store.AddMachine(Machine());

        Assert.Single(store.Machines);
        Assert.Equal(1, raised);
        Assert.True(File.Exists(_storagePath));
    }

    [Fact]
    public void UpdateMachine_ReplacesAndResetsStatus()
    {
        RemoteMachine machine = Machine();
        using RemoteMachineStore store = CreateStore();
        store.AddMachine(machine);

        store.UpdateMachine(machine with { Name = "renamed", Port = 9091 });

        Assert.Equal("renamed", store.Machines.Single().Name);
        Assert.Equal(9091, store.Machines.Single().Port);
    }

    [Fact]
    public void UpdateMachine_UnknownIdIgnored()
    {
        using RemoteMachineStore store = CreateStore();
        store.AddMachine(Machine());

        store.UpdateMachine(Machine(host: "other"));   // 不同 id

        Assert.Single(store.Machines);
        Assert.Equal("192.168.1.10", store.Machines.Single().Host);
    }

    [Fact]
    public void RemoveMachine_ClearsActiveTargetAndPersists()
    {
        RemoteMachine machine = Machine();
        using RemoteMachineStore store = CreateStore();
        store.AddMachine(machine);
        store.SelectTarget(machine.Id);

        store.RemoveMachine(machine.Id);

        Assert.Empty(store.Machines);
        Assert.Null(store.ActiveMachineId);
        Assert.False(store.IsRemoteTarget);

        // 重载验证落盘（active target 清空）
        using var reloaded = CreateStore();
        Assert.Empty(reloaded.Machines);
        Assert.Null(reloaded.ActiveMachineId);
    }

    [Fact]
    public void SelectTarget_SwitchesIsRemoteTarget()
    {
        RemoteMachine machine = Machine();
        using RemoteMachineStore store = CreateStore();
        store.AddMachine(machine);

        Assert.False(store.IsRemoteTarget);

        store.SelectTarget(machine.Id);
        Assert.True(store.IsRemoteTarget);
        Assert.Equal(machine.Id, store.ActiveMachineId);

        store.SelectTarget(null);
        Assert.False(store.IsRemoteTarget);
    }

    [Fact]
    public void Persistence_RoundTripsAcrossReload()
    {
        RemoteMachine machine = Machine(secret: "s3cret", useHttps: true);
        using (RemoteMachineStore store = CreateStore())
        {
            store.AddMachine(machine);
            store.SelectTarget(machine.Id);
        }

        using RemoteMachineStore reloaded = CreateStore();
        RemoteMachine loaded = reloaded.Machines.Single();
        Assert.Equal(machine.Id, loaded.Id);
        Assert.Equal("s3cret", loaded.Secret);
        Assert.True(loaded.UseHttps);
        Assert.Equal("https://192.168.1.10:9090", loaded.ControllerAddress);
        Assert.Equal("192.168.1.10:9090", loaded.DisplayAddress);
        Assert.Equal(machine.Id, reloaded.ActiveMachineId);
    }

    [Fact]
    public async Task Probe_ConnectedWithVersion()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson("""{"version":"v1.19.0"}""");
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine();
        store.AddMachine(machine);

        MachineConnectionStatus status = await store.RefreshConnectivityAsync(machine);

        Assert.Equal(MachineConnectionKind.Connected, status.Kind);
        Assert.Equal("v1.19.0", status.Version);
    }

    [Fact]
    public async Task Probe_ConnectedOkWhenVersionMissing()
    {
        // 源：json 无 version → connected("OK")
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson("""{"meta":true}""");
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine();
        store.AddMachine(machine);

        MachineConnectionStatus status = await store.RefreshConnectivityAsync(machine);

        Assert.Equal(MachineConnectionKind.Connected, status.Kind);
        Assert.Equal("OK", status.Version);
    }

    [Fact]
    public async Task Probe_AuthFailedOn401()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson("""{"message":"unauthorized"}""", HttpStatusCode.Unauthorized);
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine();
        store.AddMachine(machine);

        MachineConnectionStatus status = await store.RefreshConnectivityAsync(machine);

        Assert.Equal(MachineConnectionKind.Failed, status.Kind);
        Assert.Equal("Auth failed (401)", status.Reason);
    }

    [Fact]
    public async Task Probe_HttpErrorCarriesStatusCode()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson("{}", HttpStatusCode.InternalServerError);
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine();
        store.AddMachine(machine);

        MachineConnectionStatus status = await store.RefreshConnectivityAsync(machine);

        Assert.Equal(MachineConnectionKind.Failed, status.Kind);
        Assert.Equal("HTTP 500", status.Reason);
    }

    [Fact]
    public async Task Probe_ConnectionRefused()
    {
        // 源：cannotConnectToHost → failed("Connection refused")
        using StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(
            Error: StubHttpMessageHandler.ConnectionError()));
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine();
        store.AddMachine(machine);

        MachineConnectionStatus status = await store.RefreshConnectivityAsync(machine);

        Assert.Equal(MachineConnectionKind.Failed, status.Kind);
        Assert.Equal("Connection refused", status.Reason);
    }

    [Fact]
    public async Task Probe_UnknownMachineReturnsUnknown()
    {
        using RemoteMachineStore store = CreateStore();
        MachineConnectionStatus status = await store.RefreshConnectivityAsync(Machine());

        Assert.Equal(MachineConnectionKind.Unknown, status.Kind);
    }

    [Fact]
    public async Task Probe_HttpsSchemePreservedAndHttpImplied()
    {
        // https 机器探测走 https URL；http 机器自动补 scheme（源：base 推导）
        Uri? requestedUrl = null;
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Handler: (request, _) =>
            {
                requestedUrl = request.RequestUri;
                return Task.FromResult(StubHttpMessageHandler.Json("""{"version":"v1"}"""));
            }))
        {
            RepeatLastStep = true,
        };
        using RemoteMachineStore store = CreateStore(handler);
        store.AddMachine(Machine(useHttps: true));
        store.AddMachine(Machine(host: "192.168.1.11"));

        await store.RefreshConnectivityAsync(store.Machines[0]);
        Assert.Equal("https", requestedUrl!.Scheme);

        await store.RefreshConnectivityAsync(store.Machines[1]);
        Assert.Equal("http", requestedUrl!.Scheme);
    }
}
