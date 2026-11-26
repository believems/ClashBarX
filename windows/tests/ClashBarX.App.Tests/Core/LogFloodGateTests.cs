using System.Diagnostics;
using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>日志防洪门规格测试（源：CoreService.swift 的 LogFloodGate）。</summary>
public class LogFloodGateTests
{
    /// <summary>1 秒（Stopwatch 时间戳单位）。</summary>
    private static readonly long OneSecond = Stopwatch.Frequency;

    [Fact]
    public void 窗口内两百行全部放行()
    {
        var gate = new LogFloodGate();
        var lines = Enumerable.Range(1, 200).Select(i => $"line-{i}").ToArray();

        var accepted = gate.Accept(lines, timestamp: 0);

        Assert.Equal(200, accepted.Count);
    }

    [Fact]
    public void 超过两百行的部分被丢弃并在下个窗口汇报()
    {
        var gate = new LogFloodGate();
        var lines = Enumerable.Range(1, 205).Select(i => $"line-{i}").ToArray();

        var first = gate.Accept(lines, timestamp: 0);
        Assert.Equal(200, first.Count);

        // 下一秒窗口：先汇报丢弃计数，再正常放行
        var second = gate.Accept(new[] { "next-line" }, timestamp: OneSecond + 1);
        Assert.Equal(2, second.Count);
        Assert.Equal("[mihomo log] dropped 5 flooding lines", second[0]);
        Assert.Equal("next-line", second[1]);
    }

    [Fact]
    public void 相邻重复行被折叠()
    {
        var gate = new LogFloodGate();

        var accepted = gate.Accept(new[] { "same", "same", "same", "other" }, timestamp: 0);

        Assert.Equal(new[] { "same", "other" }, accepted);
    }

    [Fact]
    public void 非相邻重复不折叠()
    {
        var gate = new LogFloodGate();

        var accepted = gate.Accept(new[] { "a", "b", "a" }, timestamp: 0);

        Assert.Equal(new[] { "a", "b", "a" }, accepted);
    }

    [Fact]
    public void 窗口滚动重置计数与去重基线()
    {
        var gate = new LogFloodGate();

        var first = gate.Accept(new[] { "dup", "dup" }, timestamp: 0);
        Assert.Single(first);

        // 相邻重复也计入 dropped，跨窗口时先汇报；去重基线重置后同样的行重新放行
        var second = gate.Accept(new[] { "dup" }, timestamp: OneSecond + 1);
        Assert.Equal(new[] { "[mihomo log] dropped 1 flooding lines", "dup" }, second);
    }

    [Fact]
    public void 无丢弃时不产生汇报行()
    {
        var gate = new LogFloodGate();
        gate.Accept(new[] { "a" }, timestamp: 0);

        var second = gate.Accept(new[] { "b" }, timestamp: OneSecond + 1);

        Assert.Equal(new[] { "b" }, second);
    }
}
