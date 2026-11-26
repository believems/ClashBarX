namespace ClashBarX.Core.Tests.TestInfrastructure;

/// <summary>
/// 手动控制的 delay 替身：记录每次被请求的延迟时长，由测试端显式「放行时间流逝」。
/// 用于确定性地验证重连退避序列。
/// </summary>
public sealed class ManualDelayController
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _current;

    public List<TimeSpan> RequestedDelays { get; } = new();

    public Func<TimeSpan, CancellationToken, Task> AsFunc() => DelayAsync;

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            RequestedDelays.Add(delay);
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = completion;
        }

        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    /// <summary>等待第 <paramref name="count"/> 个延迟请求出现（轮询实现，5 秒超时）。</summary>
    public async Task WaitForDelayCountAsync(int count, string description = "delay 请求")
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (RequestedDelays.Count >= count)
                {
                    return;
                }
            }

            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"等待超时：第 {count} 个 {description}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>放行当前挂起的延迟（模拟时间流逝）。</summary>
    public void CompleteCurrentDelay()
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            completion = _current;
            _current = null;
        }

        completion?.TrySetResult(true);
    }
}
