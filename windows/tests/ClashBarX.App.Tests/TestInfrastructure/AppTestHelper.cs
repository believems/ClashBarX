using System.Diagnostics;
using System.IO;

namespace ClashBarX.App.Tests.TestInfrastructure;

/// <summary>进程类测试的等待与假内核（批处理）工厂。</summary>
public static class AppTestHelper
{
    /// <summary>轮询等待条件成立（5 秒超时，避免测试挂死）。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"等待超时：{description}");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>等待指定 pid 的进程退出（进程已不存在视为已退出）。</summary>
    public static async Task WaitProcessExitedAsync(int processId, string description)
    {
        await WaitUntilAsync(() =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;   // 进程已不存在
            }
        }, description);
    }

    /// <summary>
    /// 删除临时目录（带重试）：树杀传播是异步的，垂死孙进程的 CWD 可能短暂锁住目录。
    /// 重试后仍失败则放弃——%TEMP% 残留无害，不应掩盖测试主体结果。
    /// </summary>
    public static void CleanupDirectory(string directory)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>生成临时目录与其中的假内核批处理（存活型：挂 300 秒）。</summary>
    public static (string Directory, string BinaryPath) CreateSleepCore(string fileName = "fake-mihomo.bat")
    {
        string directory = Path.Combine(Path.GetTempPath(), "clashbarx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string binaryPath = Path.Combine(directory, fileName);
        File.WriteAllText(binaryPath, "@ping -n 300 127.0.0.1 > nul\r\n");
        return (directory, binaryPath);
    }

    /// <summary>生成「输出两行后存活」的假内核（验证 stdout/stderr 转发）。</summary>
    public static string CreateEchoCore(string directory)
    {
        string binaryPath = Path.Combine(directory, "fake-echo.bat");
        File.WriteAllText(binaryPath, "@echo line-one\r\n@echo line-two\r\n@ping -n 300 127.0.0.1 > nul\r\n");
        return binaryPath;
    }

    /// <summary>生成「以退出码 3 退出」的假内核（验证意外终止回调）。</summary>
    public static string CreateExitCore(string directory, int exitCode = 3)
    {
        string binaryPath = Path.Combine(directory, $"fake-exit{exitCode}.bat");
        File.WriteAllText(binaryPath, $"@exit /b {exitCode}\r\n");
        return binaryPath;
    }

    /// <summary>生成回显启动参数的假内核（验证 -d/-f/-ext-ctl 传参）。</summary>
    public static string CreateArgsEchoCore(string directory)
    {
        string binaryPath = Path.Combine(directory, "fake-args.bat");
        File.WriteAllText(binaryPath, "@echo d=%2 f=%4 ctl=%6\r\n@ping -n 300 127.0.0.1 > nul\r\n");
        return binaryPath;
    }

    /// <summary>
    /// 生成会派生孙进程并把孙 pid 写入 child-pid.txt 的假内核（验证进程树终结与 Job 兜底）。
    /// </summary>
    public static (string BinaryPath, string ChildPidFile) CreateTreeCore(string directory)
    {
        string binaryPath = Path.Combine(directory, "fake-tree.bat");
        string childPidFile = Path.Combine(directory, "child-pid.txt");
        File.WriteAllText(
            binaryPath,
            "@powershell -NoProfile -Command \"$p = Start-Process cmd -ArgumentList '/c','ping -n 300 127.0.0.1' -WindowStyle Hidden -PassThru; Set-Content -LiteralPath '"
            + childPidFile.Replace("'", "''")
            + "' -Value $p.Id\"\r\n@ping -n 300 127.0.0.1 > nul\r\n");
        return (binaryPath, childPidFile);
    }
}
