using System.Diagnostics;
using System.IO;
using ClashBarX.App.Core;
using ClashBarX.App.Tests.TestInfrastructure;

namespace ClashBarX.App.Tests.Core;

/// <summary>
/// MihomoProcessManager 行为规格测试（源：CoreService.swift 的 MihomoProcessManager）。
/// 用批处理假内核做真实子进程验证：启动参数、幂等、终止、意外退出回调、
/// 日志转发、Job Object 崩溃兜底与进程树终结。仅可在 Windows 上运行。
/// </summary>
public class MihomoProcessManagerTests
{
    [Fact]
    public async Task 启动假内核后进入运行状态()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            using var manager = new MihomoProcessManager(binaryPath);
            var status = await manager.StartAsync(
                Path.Combine(directory, "config.yaml"), workingDirectory: directory, controller: "127.0.0.1:9090");

            Assert.Equal(CoreLifecycleStatus.Running, status);
            Assert.True(manager.IsRunning);
            Assert.NotNull(manager.RunningProcessId);

            await manager.StopAsync();
            Assert.Equal(CoreLifecycleStatus.Stopped, manager.Status);
            Assert.False(manager.IsRunning);
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task 重复启动幂等不产生第二个进程()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            using var manager = new MihomoProcessManager(binaryPath);
            string configPath = Path.Combine(directory, "config.yaml");
            var first = await manager.StartAsync(configPath, directory, "127.0.0.1:9090");
            var second = await manager.StartAsync(configPath, directory, "127.0.0.1:9090");

            Assert.Equal(first, second);
            Assert.Equal(CoreLifecycleStatus.Running, second);
            int? pid = manager.RunningProcessId;
            await manager.StartAsync(configPath, directory, "127.0.0.1:9090");
            Assert.Equal(pid, manager.RunningProcessId);

            await manager.StopAsync();
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task 停止后进程实际退出且回调停止日志()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            var logs = new List<string>();
            using var manager = new MihomoProcessManager(binaryPath) { OnLog = logs.Add };
            await manager.StartAsync(Path.Combine(directory, "config.yaml"), directory, "127.0.0.1:9090");
            int pid = manager.RunningProcessId!.Value;

            await manager.StopAsync();

            await AppTestHelper.WaitProcessExitedAsync(pid, "停止后子进程退出");
            Assert.Equal(CoreLifecycleStatus.Stopped, manager.Status);
            Assert.Contains(logs, line => line.Contains("[mihomo stop]"));
            Assert.Contains(logs, line => line.Contains("[mihomo stopped]"));
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task 内核意外退出触发终止回调()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            string exitCore = AppTestHelper.CreateExitCore(directory, exitCode: 3);
            var terminations = new List<int>();
            using var manager = new MihomoProcessManager(exitCore) { OnTermination = terminations.Add };

            await manager.StartAsync(Path.Combine(directory, "config.yaml"), directory, "127.0.0.1:9090");

            await AppTestHelper.WaitUntilAsync(
                () => manager.Status == CoreLifecycleStatus.Stopped, "意外退出后状态归 Stopped");
            Assert.Equal(new[] { 3 }, terminations);
            Assert.False(manager.IsRunning);
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task 启动参数按规格传递()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            string argsCore = AppTestHelper.CreateArgsEchoCore(directory);
            var logs = new List<string>();
            using var manager = new MihomoProcessManager(argsCore) { OnLog = logs.Add };

            string configPath = Path.Combine(directory, "config.yaml");
            await manager.StartAsync(configPath, directory, "127.0.0.1:9090");

            await AppTestHelper.WaitUntilAsync(
                () => logs.Any(line => line.Contains("ctl=")), "参数回显行到达");
            string echoLine = logs.First(line => line.Contains("ctl="));
            Assert.Contains(directory, echoLine);
            Assert.Contains(configPath, echoLine);
            Assert.Contains("127.0.0.1:9090", echoLine);

            await manager.StopAsync();
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task 子进程标准输出经OnLog转发()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            string echoCore = AppTestHelper.CreateEchoCore(directory);
            var logs = new List<string>();
            using var manager = new MihomoProcessManager(echoCore) { OnLog = logs.Add };

            await manager.StartAsync(Path.Combine(directory, "config.yaml"), directory, "127.0.0.1:9090");

            await AppTestHelper.WaitUntilAsync(
                () => logs.Contains("line-two"), "两行输出均到达");
            await manager.StopAsync();
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task StopImmediately在紧预算内同步终止()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            using var manager = new MihomoProcessManager(binaryPath);
            await manager.StartAsync(Path.Combine(directory, "config.yaml"), directory, "127.0.0.1:9090");
            int pid = manager.RunningProcessId!.Value;

            var watch = Stopwatch.StartNew();
            manager.StopImmediately();
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"StopImmediately 耗时 {watch.Elapsed}");
            await AppTestHelper.WaitProcessExitedAsync(pid, "立即停止后子进程退出");
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task 内核二进制缺失抛解析异常并记录()
    {
        (string directory, string binaryPath) = AppTestHelper.CreateSleepCore();
        try
        {
            var logs = new List<string>();
            using var manager = new MihomoProcessManager(Path.Combine(directory, "absent.exe")) { OnLog = logs.Add };

            await Assert.ThrowsAsync<MihomoBinaryResolutionException>(
                () => manager.StartAsync(Path.Combine(directory, "config.yaml"), directory, "127.0.0.1:9090"));

            Assert.Equal(CoreLifecycleStatus.Failed, manager.Status);
            Assert.Contains(logs, line => line.Contains("[mihomo error]"));
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public async Task Job兜底_仅关闭Job句柄进程树被系统终结()
    {
        // 模拟宿主崩溃路径：不经 MihomoProcessManager 的 Kill，直接对 Job 验证 kill-on-close
        (string directory, string _) = AppTestHelper.CreateSleepCore();
        try
        {
            (string treeCore, string childPidFile) = AppTestHelper.CreateTreeCore(directory);
            var startInfo = new ProcessStartInfo(treeCore)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo)!;
            using var guard = new JobObjectGuard();
            Assert.True(guard.TryAssign(process));

            // 放宽预算：派生孙进程要冷启动 PowerShell，关 Job 句柄后进程树由系统异步终结，
            // CI 负载下都明显慢于本机；紧预算（5 秒）会偶发超时
            const int systemTerminationBudgetMilliseconds = 20000;

            // pid 文件须读到合法整数才算就绪（写入方先建文件后写内容，File.Exists 后立刻读会撞上占用）
            int childPid = await AppTestHelper.WaitForPidFileAsync(
                childPidFile, "孙进程 pid 文件可读", systemTerminationBudgetMilliseconds);
            int pid = process.Id;

            guard.Dispose();   // 宿主崩溃等价：只关 Job 句柄

            await AppTestHelper.WaitProcessExitedAsync(pid, "Job 关闭后主进程终结", systemTerminationBudgetMilliseconds);
            await AppTestHelper.WaitProcessExitedAsync(childPid, "Job 关闭后孙进程终结", systemTerminationBudgetMilliseconds);
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Theory]
    [InlineData("C:\\app\\config.yaml", "C:\\app")]
    [InlineData("C:\\app\\config\\runtime.yaml", "C:\\app")]
    [InlineData("C:\\app\\configs\\runtime.yaml", "C:\\app\\configs")]
    [InlineData("C:\\app\\sub\\profile.yaml", "C:\\app\\sub")]
    public void 工作目录按配置路径推导(string configPath, string expected)
    {
        Assert.Equal(expected, MihomoProcessManager.ResolveWorkingDirectory(configPath));
    }
}
