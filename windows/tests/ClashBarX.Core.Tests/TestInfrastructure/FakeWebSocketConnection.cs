using ClashBarX.Core.Mihomo;

namespace ClashBarX.Core.Tests.TestInfrastructure;

/// <summary>
/// 可脚本化的 WebSocket 替身：接收步骤（消息或异常）按序出队；没有待处理步骤时
/// ReceiveAsync 挂起，新步骤入队即被唤醒。ConnectAsync 默认立即成功。
/// </summary>
public sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Queue<(WebSocketStreamMessage? Message, Exception? Error)> _pending = new();
    private TaskCompletionSource<(WebSocketStreamMessage? Message, Exception? Error)>? _waiter;
    private readonly object _gate = new();

    public int ConnectCount { get; private set; }

    public int ReceiveCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>ConnectAsync 抛出的异常；null 表示连接成功。</summary>
    public Exception? ConnectError { get; set; }

    public void EnqueueMessage(WebSocketStreamMessage message) => Enqueue((message, null));

    public void EnqueueError(Exception error) => Enqueue((null, error));

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        return ConnectError == null
            ? Task.CompletedTask
            : Task.FromException(ConnectError);
    }

    public Task<WebSocketStreamMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<(WebSocketStreamMessage? Message, Exception? Error)> waiter;
        lock (_gate)
        {
            ReceiveCount++;
            if (_pending.Count > 0)
            {
                (WebSocketStreamMessage? message, Exception? error) = _pending.Dequeue();
                return error != null
                    ? Task.FromException<WebSocketStreamMessage>(error)
                    : Task.FromResult(message!.Value);
            }

            waiter = new TaskCompletionSource<(WebSocketStreamMessage?, Exception?)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiter = waiter;
        }

        cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return ConsumeAsync(waiter.Task);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private void Enqueue((WebSocketStreamMessage? Message, Exception? Error) item)
    {
        TaskCompletionSource<(WebSocketStreamMessage? Message, Exception? Error)>? waiter;
        lock (_gate)
        {
            if (_waiter != null)
            {
                waiter = _waiter;
                _waiter = null;
            }
            else
            {
                _pending.Enqueue(item);
                return;
            }
        }

        waiter.TrySetResult(item);
    }

    private static async Task<WebSocketStreamMessage> ConsumeAsync(
        Task<(WebSocketStreamMessage? Message, Exception? Error)> task)
    {
        (WebSocketStreamMessage? message, Exception? error) = await task;
        if (error != null)
        {
            throw error;
        }

        return message!.Value;
    }
}
