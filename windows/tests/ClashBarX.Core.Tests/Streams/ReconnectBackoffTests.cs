using ClashBarX.Core.Streams;

namespace ClashBarX.Core.Tests.Streams;

public class ReconnectBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);

    [Fact]
    public void 无抖动时延迟序列为1_2_4_8秒后恒8秒()
    {
        var backoff = new ReconnectBackoff(Base, jitter: () => 1.0);

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
    }

    [Fact]
    public void 失败计数封顶为8()
    {
        var backoff = new ReconnectBackoff(Base, jitter: () => 1.0);
        for (int i = 0; i < 20; i++)
        {
            backoff.NextDelay();
        }

        Assert.Equal(8, backoff.Attempt);
    }

    [Fact]
    public void 收到有效载荷后计数归零()
    {
        var backoff = new ReconnectBackoff(Base, jitter: () => 1.0);
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();

        backoff.Reset();

        Assert.Equal(0, backoff.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [Fact]
    public void 抖动下限被夹取到基准延迟()
    {
        // 1s × 0.85 = 0.85s → 夹回 1s
        var backoff = new ReconnectBackoff(Base, jitter: () => 0.85);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [Fact]
    public void 抖动上限被夹取到最大延迟()
    {
        // 8s × 1.15 = 9.2s → 夹到 8s
        var backoff = new ReconnectBackoff(Base, TimeSpan.FromSeconds(8), () => 1.15);
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();

        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
    }

    [Fact]
    public void 抖动在限内时按乘积计算()
    {
        // 2s × 0.9 = 1.8s
        var backoff = new ReconnectBackoff(Base, TimeSpan.FromSeconds(8), () => 0.9);
        backoff.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(1.8), backoff.NextDelay());

        // 8s × 0.9 = 7.2s
        backoff.NextDelay();
        backoff.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(7.2), backoff.NextDelay());
    }

    [Fact]
    public void 自定义基准延迟时秒数档位不变()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(16), () => 1.0);
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(16), backoff.NextDelay());
    }
}
