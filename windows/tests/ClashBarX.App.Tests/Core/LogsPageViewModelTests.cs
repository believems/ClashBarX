using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>日志页模型规格测试（源：LogsViewModel + AppViewModel+Logging.swift 的管线语义）。</summary>
public class LogsPageViewModelTests
{
    [Fact]
    public void 应用日志直接入列_最近在前()
    {
        var viewModel = new LogsPageViewModel();

        viewModel.AppendClashBarLog("first");
        viewModel.AppendClashBarLog("second");

        Assert.Equal(2, viewModel.VisibleLogs.Count);
        Assert.Equal("second", viewModel.VisibleLogs[0].Message);
        Assert.Equal("first", viewModel.VisibleLogs[1].Message);
    }

    [Fact]
    public void mihomo日志先进缓冲_手动刷前不可见()
    {
        var viewModel = new LogsPageViewModel();

        viewModel.QueueMihomoLog("info", "buffered line");
        Assert.Empty(viewModel.VisibleLogs);

        viewModel.FlushPendingMihomoLogs();
        LogEntryViewModel entry = Assert.Single(viewModel.VisibleLogs);
        Assert.Equal("buffered line", entry.Message);
        Assert.Equal("Mihomo", entry.Source);
    }

    [Fact]
    public void 缓冲满40条立即刷()
    {
        var viewModel = new LogsPageViewModel();

        for (int i = 0; i < LogsPageViewModel.MaxBufferedMihomoLogEntries - 1; i++)
        {
            viewModel.QueueMihomoLog("info", $"line-{i}");
        }

        Assert.Empty(viewModel.VisibleLogs);

        viewModel.QueueMihomoLog("info", "line-40");   // 第 40 条触发满批直刷
        Assert.Equal(40, viewModel.VisibleLogs.Count);
        Assert.Equal("line-40", viewModel.VisibleLogs[0].Message);   // 最近在前
    }

    [Fact]
    public void 等级过滤_空集为全部()
    {
        var viewModel = new LogsPageViewModel();
        viewModel.AppendClashBarLog("info", "info line");
        viewModel.AppendClashBarLog("error", "error line");
        viewModel.AppendClashBarLog("warning", "warning line");
        Assert.Equal(3, viewModel.VisibleLogs.Count);

        viewModel.IsErrorSelected = true;   // 只看 error
        Assert.Equal("error line", Assert.Single(viewModel.VisibleLogs).Message);

        viewModel.IsErrorSelected = false;   // 清空选择 = 全部
        Assert.Equal(3, viewModel.VisibleLogs.Count);

        viewModel.IsWarningSelected = true;
        viewModel.IsErrorSelected = true;   // 多选：error + warning
        Assert.Equal(2, viewModel.VisibleLogs.Count);
    }

    [Fact]
    public void 搜索按消息子串过滤_忽略大小写()
    {
        var viewModel = new LogsPageViewModel();
        viewModel.AppendClashBarLog("mihomo started");
        viewModel.AppendClashBarLog("proxy guard probe");

        viewModel.SearchText = "GUARD";

        Assert.Equal("proxy guard probe", Assert.Single(viewModel.VisibleLogs).Message);
    }

    [Fact]
    public void 渲染上限120_匹配数计全量()
    {
        var viewModel = new LogsPageViewModel();

        for (int i = 0; i < 200; i++)
        {
            viewModel.AppendClashBarLog($"line-{i}");
        }

        Assert.Equal(LogsPageViewModel.RenderLimit, viewModel.VisibleLogs.Count);
        Assert.Equal("200", viewModel.MatchedCountText);   // 全部匹配数（徽章）
        Assert.Equal("line-199", viewModel.VisibleLogs[0].Message);   // 最新在前
        Assert.Equal("line-80", viewModel.VisibleLogs[^1].Message);   // 保留最新 120 条
    }

    [Fact]
    public void 内存上限5000_裁掉旧条目()
    {
        var viewModel = new LogsPageViewModel();

        for (int i = 0; i < LogsPageViewModel.MaxInMemoryLogEntries + 100; i++)
        {
            viewModel.AppendClashBarLog($"line-{i}");
        }

        Assert.Equal(LogsPageViewModel.MaxInMemoryLogEntries, viewModel.SnapshotEntries().Count);
        Assert.Equal($"line-{LogsPageViewModel.MaxInMemoryLogEntries + 99}", viewModel.SnapshotEntries()[0].Message);
    }

    [Fact]
    public void 面板隐藏时收紧上限到20()
    {
        var viewModel = new LogsPageViewModel();
        viewModel.QueueMihomoLog("info", "pending line");
        for (int i = 0; i < 30; i++)
        {
            viewModel.AppendClashBarLog($"line-{i}");
        }

        viewModel.SetPanelVisible(false);   // 先刷缓冲再按隐藏上限裁剪

        // 内存条目裁到 20（渲染上限 120 不低于此，可见即全部 20 条）
        Assert.Equal(LogsPageViewModel.HiddenPanelMaxInMemoryLogEntries, viewModel.SnapshotEntries().Count);
        Assert.Equal(LogsPageViewModel.HiddenPanelMaxInMemoryLogEntries, viewModel.VisibleLogs.Count);
        Assert.Equal("pending line", viewModel.VisibleLogs[0].Message);   // 最新在前
    }

    [Fact]
    public void 清空包含缓冲中的日志()
    {
        var viewModel = new LogsPageViewModel();
        viewModel.AppendClashBarLog("visible");
        viewModel.QueueMihomoLog("info", "pending");

        viewModel.ClearAll();
        viewModel.FlushPendingMihomoLogs();   // 缓冲也清了，不再出现

        Assert.Empty(viewModel.VisibleLogs);
        Assert.Equal("0", viewModel.MatchedCountText);
    }

    [Fact]
    public void 复制文本包含全部条目格式()
    {
        var viewModel = new LogsPageViewModel();
        viewModel.AppendClashBarLog("hello");
        viewModel.QueueMihomoLog("warning", "careful");
        viewModel.FlushPendingMihomoLogs();

        string text = viewModel.CopyAllText();
        Assert.Contains("[ClashBar] info: hello", text);
        Assert.Contains("[Mihomo] warning: careful", text);
    }

    [Fact]
    public void 空消息不入列()
    {
        var viewModel = new LogsPageViewModel();

        viewModel.AppendClashBarLog("");
        viewModel.QueueMihomoLog("info", "");

        Assert.Empty(viewModel.VisibleLogs);
    }
}
