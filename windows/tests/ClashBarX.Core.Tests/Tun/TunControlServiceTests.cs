using System.Text.Json;
using System.Text.Json.Nodes;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Tun;
using ClashBarX.Core.Versioning;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Tun;

/// <summary>TUN 业务规格测试（源：AppViewModel+Tun.swift 逐行对照：PATCH→GET 验证链、mips 门槛、启动恢复）。</summary>
public class TunControlServiceTests
{
    private sealed class FakeApiClient : IMihomoApiClient
    {
        public List<MihomoEndpoint> Requests { get; } = new();

        /// <summary>按脚本返回 GET /configs 响应；脚本耗尽后重复末条。</summary>
        public Queue<ConfigSnapshot> SnapshotScript { get; } = new();

        /// <summary>PATCH 调用的抛错（脚本化失败）。</summary>
        public Exception? PatchError { get; set; }

        public void UpdateCredentials(string controller, string? secret) { }

        public Task<T> RequestAsync<T>(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
            where T : class
        {
            Requests.Add(endpoint);
            var snapshot = SnapshotScript.Count > 0
                ? (SnapshotScript.Count > 1 ? SnapshotScript.Dequeue() : SnapshotScript.Peek())
                : new ConfigSnapshot(null, null, null, null, null, null, null, null, null, null, null, null, null, null);
            return Task.FromResult((T)(object)snapshot);
        }

        public Task RequestNoResponseAsync(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            Requests.Add(endpoint);
            if (PatchError is { } error)
            {
                throw error;
            }

            return Task.CompletedTask;
        }

        public IWebSocketConnection CreateWebSocketConnection(MihomoEndpoint endpoint) =>
            throw new NotSupportedException("TUN 测试不使用 WebSocket");

        public void Dispose() { }
    }

    /// <summary>取第 index 个请求（PATCH）的 JSON body。</summary>
    private static JsonObject PatchBody(IReadOnlyList<MihomoEndpoint> requests, int index) =>
        JsonNode.Parse(requests[index].Body!)!.AsObject();

    private static ConfigSnapshot SnapshotWithTun(bool enabled, string? stack) =>
        new(null, null, null, null, null, null, null, null, null, null,
            new ConfigSnapshot.TunConfig(enabled, stack), null, null, null);

    private static FakeApiClient ClientWith(params ConfigSnapshot[] snapshots)
    {
        var client = new FakeApiClient();
        foreach (ConfigSnapshot snapshot in snapshots)
        {
            client.SnapshotScript.Enqueue(snapshot);
        }

        return client;
    }

    [Fact]
    public async Task ToggleAsync_PatchThenVerify_AppliedWhenRuntimeMatches()
    {
        FakeApiClient client = ClientWith(SnapshotWithTun(true, "mixed"));
        var service = new TunControlService(client);

        TunToggleResult result = await service.ToggleAsync(true);

        Assert.Equal(TunToggleOutcome.Applied, result.Outcome);
        Assert.True(result.State.Enabled);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(MihomoEndpointKind.PatchConfigs, client.Requests[0].Kind);
        Assert.Equal(MihomoEndpointKind.GetConfigs, client.Requests[1].Kind);
    }

    [Fact]
    public async Task ToggleAsync_RuntimeMismatch_ReportedWithoutThrowing()
    {
        // 源：actualState != enabled → log error（runtime_state_mismatch），不抛
        FakeApiClient client = ClientWith(SnapshotWithTun(false, null));
        var service = new TunControlService(client);

        TunToggleResult result = await service.ToggleAsync(true, "mixed");

        Assert.Equal(TunToggleOutcome.RuntimeStateMismatch, result.Outcome);
        Assert.False(result.State.Enabled);
    }

    [Fact]
    public async Task ToggleAsync_DisableOmitsStack()
    {
        // 源：patchTunConfig 仅 enable=true 时注入 stack；关闭时不带
        FakeApiClient client = ClientWith(SnapshotWithTun(false, null));
        var service = new TunControlService(client);

        await service.ToggleAsync(false, "mixed");

        JsonObject body = PatchBody(client.Requests, 0);
        Assert.Equal(false, (bool?)body["tun"]!["enable"]);
        Assert.Null(body["tun"]!["stack"]);
    }

    [Fact]
    public async Task ToggleAsync_PatchErrorPropagates()
    {
        // 源：PATCH 异常上抛（调用方 log error + 回读状态）
        FakeApiClient client = ClientWith(SnapshotWithTun(true, null));
        client.PatchError = new HttpRequestException("connection refused");
        var service = new TunControlService(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ToggleAsync(false));
    }

    [Fact]
    public async Task SelectStackAsync_PatchesEnableWithStack()
    {
        FakeApiClient client = ClientWith(SnapshotWithTun(true, "system"));
        var service = new TunControlService(client);

        TunToggleResult result = await service.SelectStackAsync("system");

        Assert.Equal(TunToggleOutcome.Applied, result.Outcome);
        JsonObject body = PatchBody(client.Requests, 0);
        Assert.Equal(true, (bool?)body["tun"]!["enable"]);
        Assert.Equal("system", (string?)body["tun"]!["stack"]);
    }

    [Fact]
    public async Task FetchRuntimeState_NullTunMeansDisabled()
    {
        FakeApiClient client = new();   // 无脚本 → 默认快照（tun=null）
        var service = new TunControlService(client);

        TunRuntimeState state = await service.FetchRuntimeStateAsync();

        Assert.False(state.Enabled);
        Assert.Null(state.Stack);
    }

    [Fact]
    public async Task RestoreAtStartup_AlreadyEnabled_ConfirmedWithoutPatch()
    {
        FakeApiClient client = ClientWith(SnapshotWithTun(true, "gvisor"));
        var service = new TunControlService(client);

        TunToggleResult result = await service.RestoreAtStartupAsync(savedEnabled: true, preferredStack: null);

        Assert.Equal(TunToggleOutcome.Applied, result.Outcome);
        Assert.Single(client.Requests);
        Assert.Equal(MihomoEndpointKind.GetConfigs, client.Requests[0].Kind);
    }

    [Fact]
    public async Task RestoreAtStartup_SavedEnabledButRuntimeOff_PatchesOnAndVerifies()
    {
        // GET(off) → PATCH(enable+stack) → GET(on)
        FakeApiClient client = ClientWith(SnapshotWithTun(false, null), SnapshotWithTun(true, "mixed"));
        var service = new TunControlService(client);

        TunToggleResult result = await service.RestoreAtStartupAsync(savedEnabled: true, preferredStack: "mixed");

        Assert.Equal(TunToggleOutcome.Applied, result.Outcome);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(MihomoEndpointKind.GetConfigs, client.Requests[0].Kind);
        Assert.Equal(MihomoEndpointKind.PatchConfigs, client.Requests[1].Kind);
        Assert.Equal(MihomoEndpointKind.GetConfigs, client.Requests[2].Kind);
    }

    [Fact]
    public async Task RestoreAtStartup_SavedDisabled_ReturnsActualState()
    {
        FakeApiClient client = ClientWith(SnapshotWithTun(true, "mixed"));
        var service = new TunControlService(client);

        TunToggleResult result = await service.RestoreAtStartupAsync(savedEnabled: false, preferredStack: null);

        Assert.Equal(TunToggleOutcome.Applied, result.Outcome);
        Assert.True(result.State.Enabled);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task AlignStackIfEnabled_SkipsWhenDisabledOrSameStack()
    {
        FakeApiClient client = ClientWith(SnapshotWithTun(false, null));
        var service = new TunControlService(client);

        bool aligned = await service.AlignStackIfEnabledAsync("mixed");

        Assert.False(aligned);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task AlignStackIfEnabled_PatchesStackOnly()
    {
        // 源：ensureTunStackOnStartupIfNeeded 只 PATCH stack，不动 enable
        FakeApiClient client = ClientWith(SnapshotWithTun(true, "gvisor"), SnapshotWithTun(true, "mixed"));
        var service = new TunControlService(client);

        bool aligned = await service.AlignStackIfEnabledAsync("mixed");

        Assert.True(aligned);
        JsonObject body = PatchBody(client.Requests, 1);
        Assert.Null(body["tun"]!["enable"]);   // 源：只 PATCH stack，不动 enable
        Assert.Equal("mixed", (string?)body["tun"]!["stack"]);
    }

    [Fact]
    public async Task AlignStackIfEnabled_IgnoresCaseDifference()
    {
        FakeApiClient client = ClientWith(SnapshotWithTun(true, "Mixed"));
        var service = new TunControlService(client);

        bool aligned = await service.AlignStackIfEnabledAsync("mixed");

        Assert.False(aligned);   // 源：caseInsensitiveCompare 相同即跳过
    }

    [Fact]
    public void IsMipsStackSupported_VersionGates()
    {
        // 源：mipsStackMinimumCoreVersion = 1.19.31，版本不可解析 → false
        Assert.False(TunControlService.IsMipsStackSupported(null));
        Assert.False(TunControlService.IsMipsStackSupported(""));
        Assert.False(TunControlService.IsMipsStackSupported("garbage"));
        Assert.False(TunControlService.IsMipsStackSupported("1.19.30"));
        Assert.True(TunControlService.IsMipsStackSupported("1.19.31"));
        Assert.True(TunControlService.IsMipsStackSupported("v1.19.32"));
        Assert.True(TunControlService.IsMipsStackSupported("1.20.0"));
        Assert.True(TunControlService.IsMipsStackSupported("1.19.31-alpha"));   // 预发布段剥离
        Assert.True(TunControlService.IsMipsStackSupported("2.0"));
    }
}
