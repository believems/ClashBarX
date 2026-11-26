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

    /// <summary>挂起直到 gate 释放才返回响应（模拟慢请求）。</summary>
    private static async Task<HttpResponseMessage> BlockThenRespondAsync(
        TaskCompletionSource gate, string json)
    {
        await gate.Task;
        return StubHttpMessageHandler.Json(json);
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
    public async Task Probe_StaleGenerationResultDoesNotOverwriteNewerState()
    {
        // 源：startProbe 的 generation 防竞态——旧代回包（慢请求）不得覆盖新一轮结果
        var slowGate = new TaskCompletionSource();
        int calls = 0;
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Handler: (_, _) => Interlocked.Increment(ref calls) == 1
                ? BlockThenRespondAsync(slowGate, """{"version":"stale"}""")
                : Task.FromResult(StubHttpMessageHandler.Json("""{"version":"latest"}"""))))
        {
            RepeatLastStep = true,
        };
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine(name: "race");
        store.AddMachine(machine);

        Task<MachineConnectionStatus> first = store.RefreshConnectivityAsync(machine);
        await Task.Delay(50);   // 让第一代先发出并挂起
        Task<MachineConnectionStatus> second = store.RefreshConnectivityAsync(machine);   // 第二代（取消第一代）
        MachineConnectionStatus secondStatus = await second;
        Assert.Equal(MachineConnectionKind.Connected, secondStatus.Kind);
        Assert.Equal("latest", secondStatus.Version);

        slowGate.SetResult();   // 放行第一代：其慢响应（stale）不得覆盖第二代结果
        MachineConnectionStatus firstStatus = await first;
        Assert.Equal(MachineConnectionKind.Unknown, firstStatus.Kind);   // 被取消的探测按源语义回 unknown

        MachineConnectionStatus stored = store.StatusOf(machine.Id);
        Assert.Equal(MachineConnectionKind.Connected, stored.Kind);
        Assert.Equal("latest", stored.Version);   // 旧代结果被丢弃，仍是第二代结果
    }

    [Fact]
    public async Task Probe_RemovedMachineResultIsDiscarded()
    {
        // 源：机器被删除后回包丢弃（_machines.contains 守卫）
        var gate = new TaskCompletionSource();
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Handler: (_, _) => BlockThenRespondAsync(gate, """{"version":"late"}""")));
        using RemoteMachineStore store = CreateStore(handler);
        RemoteMachine machine = Machine(name: "doomed");
        store.AddMachine(machine);

        Task<MachineConnectionStatus> probe = store.RefreshConnectivityAsync(machine);
        await Task.Delay(50);
        store.RemoveMachine(machine.Id);   // 探测中删除
        gate.SetResult();
        MachineConnectionStatus status = await probe;

        Assert.Equal(MachineConnectionKind.Unknown, status.Kind);   // 删除后的回包不落状态
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
