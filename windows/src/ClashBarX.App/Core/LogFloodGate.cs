using System.Diagnostics;

namespace ClashBarX.App.Core;

/// <summary>
/// mihomo 日志防洪门（源：CoreService.swift 的 LogFloodGate）。
/// 每秒窗口最多放行 200 行；相邻重复行折叠；超限丢弃并在窗口滚动时
/// 汇报一条 "[mihomo log] dropped N flooding lines"。时间用 Stopwatch 单调钟。
/// </summary>
public sealed class LogFloodGate
{
    private const int MaxLinesPerWindow = 200;
    private static readonly long WindowTicks = Stopwatch.Frequency;   // 1 秒

    private long _windowStartTimestamp;
    private int _emitted;
    private int _dropped;
    private string? _lastLine;

    /// <summary>批量收行，返回本批应放行的行（含窗口滚动时的 dropped 汇报行）。</summary>
    public IReadOnlyList<string> Accept(IReadOnlyList<string> lines, long? timestamp = null)
    {
        long now = timestamp ?? Stopwatch.GetTimestamp();
        var accepted = new List<string>(lines.Count);
        foreach (string line in lines)
        {
            if (now - _windowStartTimestamp >= WindowTicks)
            {
                if (_dropped > 0)
                {
                    accepted.Add($"[mihomo log] dropped {_dropped} flooding lines");
                }

                _windowStartTimestamp = now;
                _emitted = 0;
                _dropped = 0;
                _lastLine = null;
            }

            if (line == _lastLine || _emitted >= MaxLinesPerWindow)
            {
                _dropped++;
                continue;
            }

            _lastLine = line;
            _emitted++;
            accepted.Add(line);
        }

        return accepted;
    }
}
