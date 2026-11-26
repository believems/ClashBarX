namespace ClashBarX.Core.Proxies;

/// <summary>
/// 延迟分级（源：Models/LatencyLevel.swift——阈值 200/500/1000，0 与 null = timeout）。
/// 分级只做归类；颜色与信号条高度由展示层按分级取值。
/// </summary>
public enum LatencyLevel
{
    /// <summary>&lt;200ms。</summary>
    Excellent,

    /// <summary>≥200ms、&lt;500ms。</summary>
    Good,

    /// <summary>≥500ms、&lt;1000ms。</summary>
    Fair,

    /// <summary>≥1000ms。</summary>
    Poor,

    /// <summary>测速超时（接口用 0 表示）或未测。</summary>
    Timeout,
}

public static class LatencyLevelExtensions
{
    /// <summary>信号条高度（源：LatencySignalBars.barHeight——越慢越高，超时最高）。</summary>
    public static double SignalBarHeight(this LatencyLevel level) => level switch
    {
        LatencyLevel.Excellent => 5,
        LatencyLevel.Good => 7.5,
        LatencyLevel.Fair => 10,
        _ => 13,
    };

    /// <summary>展示颜色（源：latencyColor——绿/青/黄/红；未测用弱灰）。</summary>
    public static string ColorHex(this LatencyLevel level) => level switch
    {
        LatencyLevel.Excellent => "#34A853",
        LatencyLevel.Good => "#0E9AA7",
        LatencyLevel.Fair => "#B45309",
        _ => "#C0392B",
    };

    /// <summary>从延迟毫秒归类（源：LatencyLevel(delay:)）。</summary>
    public static LatencyLevel Of(int? delayMilliseconds) => delayMilliseconds switch
    {
        null or <= 0 => LatencyLevel.Timeout,
        < 200 => LatencyLevel.Excellent,
        < 500 => LatencyLevel.Good,
        < 1000 => LatencyLevel.Fair,
        _ => LatencyLevel.Poor,
    };
}
