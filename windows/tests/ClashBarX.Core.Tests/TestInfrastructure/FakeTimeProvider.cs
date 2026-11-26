namespace ClashBarX.Core.Tests.TestInfrastructure;

/// <summary>可手动推进时间的 TimeProvider（用于断开日志节流测试）。</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan span) => _utcNow += span;
}
