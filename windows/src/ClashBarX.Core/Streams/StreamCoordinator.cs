using ClashBarX.Core.Mihomo;

namespace ClashBarX.Core.Streams;

/// <summary>
/// WS 流协调器（源：StreamCoordinator.swift）。四条流（traffic / memory / connections / logs）
/// 共用本协调器：每条流独立维护连接、接收循环与重连去抖状态。
/// 回调（ShouldReconnect / OnDisconnect / OnStartError / onPayload）在后台线程调用，UI 层需自行编组。
/// </summary>
public sealed class StreamCoordinator : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> DefaultDelay =
        static (span, token) => Task.Delay(span, token);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _disconnectLogThrottleInterval;
    private readonly Func<double> _jitter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;

    private readonly object _lock = new();
    private readonly Dictionary<string, StreamSession> _sessions = new();
    private readonly Dictionary<string, ReconnectBackoff> _backoffs = new();
    private readonly Dictionary<string, DisconnectLogState> _disconnectLogs = new();

    public StreamCoordinator(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        TimeSpan? disconnectLogThrottleInterval = null,
        Func<double>? jitter = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(8);
        _disconnectLogThrottleInterval = disconnectLogThrottleInterval ?? TimeSpan.FromSeconds(2);
        _jitter = jitter ?? ReconnectBackoff.DefaultJitterFactory;
        _delay = delay ?? DefaultDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>是否应尝试重连（源：shouldReconnect，通常绑定「内核在跑或远程目标」）。</summary>
    public Func<bool> ShouldReconnect { get; set; } = static () => false;

    /// <summary>流断开时回调（已按 2s 同消息节流），参数为 (key, 错误描述)。</summary>
    public Action<string, string>? OnDisconnect { get; set; }

    /// <summary>建立连接失败（工厂抛错，如 URL 非法）时回调，此时不重试。</summary>
    public Action<string, Exception>? OnStartError { get; set; }

    /// <summary>
    /// 启动（或重启）一条流：先取消既有会话（保留重连计数），再建新连接并进入接收循环。
    /// 工厂抛错仅上报 OnStartError，不自动重试（源：start 的 makeWebSocket throws 分支）。
    /// </summary>
    public void Start(
        string key,
        Func<IWebSocketConnection> makeConnection,
        Func<ReadOnlyMemory<byte>, Task> onPayload,
        Func<WebSocketStreamMessage, ReadOnlyMemory<byte>?>? normalizePayload = null)
    {
        StreamSession? oldSession;
        lock (_lock)
        {
            _sessions.Remove(key, out oldSession);
        }

        // 旧接收循环感知取消后自行收尾（含连接释放）
        oldSession?.Cancellation.Cancel();

        IWebSocketConnection connection;
        try
        {
            connection = makeConnection();
        }
        catch (Exception error)
        {
            OnStartError?.Invoke(key, error);
            return;
        }

        var session = new StreamSession(
            key,
            new CancellationTokenSource(),
            makeConnection,
            onPayload,
            normalizePayload ?? StreamPayload.Normalize);

        lock (_lock)
        {
            _sessions[key] = session;
            session.CurrentConnection = connection;
        }

        // 接收循环放到线程池启动：与调用方的同步上下文解耦，
        // 避免 await continuation 被派回 UI 线程或测试框架的上下文
        session.LoopTask = Task.Run(() => RunLoopAsync(session, connection, session.Cancellation.Token));
    }

    /// <summary>停止一条流；resetReconnectState 为 false 时保留重连计数（源：cancel）。</summary>
    public void Cancel(string key, bool resetReconnectState = true)
    {
        StreamSession? session;
        lock (_lock)
        {
            _sessions.Remove(key, out session);
            if (resetReconnectState)
            {
                _backoffs.Remove(key);
                _disconnectLogs.Remove(key);
            }
        }

        session?.Cancellation.Cancel();
    }

    public void CancelAll()
    {
        string[] keys;
        lock (_lock)
        {
            keys = _sessions.Keys.ToArray();
        }

        foreach (string key in keys)
        {
            Cancel(key);
        }
    }

    /// <summary>该流是否存在已登记（未必已连通）的连接。</summary>
    public bool IsActive(string key)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(key, out var session) && session.CurrentConnection != null;
        }
    }

    /// <summary>清除某条流的重连计数与断开日志节流状态（源：clearReconnectState）。</summary>
    public void ClearReconnectState(string key)
    {
        lock (_lock)
        {
            _backoffs.Remove(key);
            _disconnectLogs.Remove(key);
        }
    }

    /// <summary>payload 规范化的便捷入口（与默认 normalizePayload 一致）。</summary>
    public static ReadOnlyMemory<byte>? NormalizePayload(WebSocketStreamMessage message) =>
        StreamPayload.Normalize(message);

    public void Dispose() => CancelAll();

    // ---- 接收循环 ----

    /// <summary>
    /// 每轮跑一条连接：连接 → 接收循环 → 断开处理。返回 true 表示应换新连接继续
    /// （对齐 Swift 的 restart → start 链路），false 表示已取消或启动失败。
    /// </summary>
    private async Task<bool> RunConnectionOnceAsync(
        StreamSession session, IWebSocketConnection connection, CancellationToken cancellationToken)
    {
        SetActiveConnection(session, connection);
        try
        {
            try
            {
                await connection.ConnectAsync(cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                return await HandleDisconnectAsync(session, connection, error, cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                WebSocketStreamMessage message;
                try
                {
                    message = await connection.ReceiveAsync(cancellationToken);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    return await HandleDisconnectAsync(session, connection, error, cancellationToken);
                }

                // 噪声 payload：不重置计数、不广播
                ReadOnlyMemory<byte>? payload = session.NormalizePayload(message);
                if (payload is null)
                {
                    continue;
                }

                MarkPayloadReceived(session.Key);
                try
                {
                    await session.OnPayload(payload.Value);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    // Swift 原版此处会让接收循环悄悄死掉并留下幽灵会话；
                    // 这里按断线处理走重连（有意修正，见迁移报告）
                    return await HandleDisconnectAsync(session, connection, error, cancellationToken);
                }
            }

            return false;
        }
        finally
        {
            SetActiveConnection(session, null);
            await connection.DisposeAsync();
        }
    }

    private async Task RunLoopAsync(
        StreamSession session, IWebSocketConnection initialConnection, CancellationToken cancellationToken)
    {
        IWebSocketConnection connection = initialConnection;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await RunConnectionOnceAsync(session, connection, cancellationToken))
                {
                    return;
                }

                // 换新连接重启：工厂失败属启动错误，不重试（对齐 start 的 makeWebSocket throws 分支）
                try
                {
                    connection = session.MakeConnection();
                }
                catch (Exception error)
                {
                    OnStartError?.Invoke(session.Key, error);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // 用户回调自身抛错等意外：终止本循环（对齐 Swift 中 Task 失败被吞的容错行为）
        }
        finally
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(session.Key, out var current) && current == session)
                {
                    _sessions.Remove(session.Key);
                }
            }

            session.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// 断开处理：释放连接、节流上报 OnDisconnect，然后进入等待循环。
    /// 返回 true 表示应重启（外层循环重建连接），false 表示已被取消。
    /// </summary>
    private async Task<bool> HandleDisconnectAsync(
        StreamSession session, IWebSocketConnection connection, Exception error, CancellationToken cancellationToken)
    {
        SetActiveConnection(session, null);
        await connection.DisposeAsync();

        string message = error.Message;
        if (ShouldLogDisconnect(session.Key, message))
        {
            OnDisconnect?.Invoke(session.Key, message);
        }

        // 源：shouldReconnect ? 退避延迟 : maxDelay 轮询等待
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay = ShouldReconnect()
                ? NextReconnectDelay(session.Key)
                : _maxDelay;
            try
            {
                await _delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (ShouldReconnect())
            {
                return true;
            }
        }

        return false;
    }

    private void SetActiveConnection(StreamSession session, IWebSocketConnection? connection)
    {
        lock (_lock)
        {
            session.CurrentConnection = connection;
        }
    }

    private void MarkPayloadReceived(string key)
    {
        lock (_lock)
        {
            GetOrCreateBackoff(key).Reset();
        }
    }

    private TimeSpan NextReconnectDelay(string key)
    {
        lock (_lock)
        {
            return GetOrCreateBackoff(key).NextDelay();
        }
    }

    private ReconnectBackoff GetOrCreateBackoff(string key)
    {
        if (!_backoffs.TryGetValue(key, out var backoff))
        {
            backoff = new ReconnectBackoff(_baseDelay, _maxDelay, _jitter);
            _backoffs[key] = backoff;
        }

        return backoff;
    }

    private bool ShouldLogDisconnect(string key, string message)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_disconnectLogs.TryGetValue(key, out var state)
                && now - state.Timestamp < _disconnectLogThrottleInterval
                && state.Message == message)
            {
                return false;
            }

            _disconnectLogs[key] = new DisconnectLogState(now, message);
            return true;
        }
    }

    private readonly record struct DisconnectLogState(DateTimeOffset Timestamp, string Message);

    private sealed class StreamSession
    {
        public StreamSession(
            string key,
            CancellationTokenSource cancellation,
            Func<IWebSocketConnection> makeConnection,
            Func<ReadOnlyMemory<byte>, Task> onPayload,
            Func<WebSocketStreamMessage, ReadOnlyMemory<byte>?> normalizePayload)
        {
            Key = key;
            Cancellation = cancellation;
            MakeConnection = makeConnection;
            OnPayload = onPayload;
            NormalizePayload = normalizePayload;
        }

        public string Key { get; }

        public CancellationTokenSource Cancellation { get; }

        public Func<IWebSocketConnection> MakeConnection { get; }

        public Func<ReadOnlyMemory<byte>, Task> OnPayload { get; }

        public Func<WebSocketStreamMessage, ReadOnlyMemory<byte>?> NormalizePayload { get; }

        /// <summary>当前已登记连接（lock 保护）；null 表示断开等待中。</summary>
        public IWebSocketConnection? CurrentConnection { get; set; }

        public Task LoopTask { get; set; } = Task.CompletedTask;
    }
}
