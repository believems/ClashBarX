namespace ClashBarX.Core.Streams;

/// <summary>
/// WS 流重连退避算法（源：StreamCoordinator.swift 的 nextReconnectDelayNanoseconds）。
/// 公式：延迟 = min(2^min(attempt, 3), 8) 秒 × jitter(默认 0.85~1.15)，再夹取到 [base, max]；
/// attempt 收到有效 payload 时重置为 0，连续失败计数封顶 8。
/// </summary>
public sealed class ReconnectBackoff
{
    /// <summary>默认抖动：[0.85, 1.15) 均匀分布（源：Double.random(in: 0.85...1.15)）。</summary>
    internal static double DefaultJitterFactory() => 0.85 + (Random.Shared.NextDouble() * 0.3);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Func<double> _jitter;

    private int _attempt;

    public ReconnectBackoff(
        TimeSpan baseDelay,
        TimeSpan? maxDelay = null,
        Func<double>? jitter = null)
    {
        _baseDelay = baseDelay;
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(8);
        _jitter = jitter ?? DefaultJitterFactory;
    }

    /// <summary>当前连续失败计数（封顶 8）。</summary>
    public int Attempt => Volatile.Read(ref _attempt);

    /// <summary>取下一次重连延迟，并递增失败计数（封顶 8）。</summary>
    public TimeSpan NextDelay()
    {
        int attempt = Math.Max(0, Volatile.Read(ref _attempt));
        int cappedShift = Math.Min(attempt, 3);
        int seconds = Math.Min(8, 1 << cappedShift);
        Volatile.Write(ref _attempt, Math.Min(attempt + 1, 8));

        long baseTicks = (long)seconds * _baseDelay.Ticks;
        // 源：UInt64(Double(base) * Double.random(...)) 的截断语义
        long jitteredTicks = (long)(baseTicks * _jitter());
        long clampedTicks = Math.Min(_maxDelay.Ticks, Math.Max(_baseDelay.Ticks, jitteredTicks));
        return TimeSpan.FromTicks(clampedTicks);
    }

    /// <summary>收到有效 payload：计数归零（源：markPayloadReceived）。</summary>
    public void Reset() => Volatile.Write(ref _attempt, 0);
}
