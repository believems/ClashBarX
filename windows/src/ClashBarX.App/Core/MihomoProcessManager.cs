using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ClashBarX.App.Core;

/// <summary>内核生命周期状态（源：CoreService.swift 的 CoreLifecycleStatus）。</summary>
public enum CoreLifecycleStatus
{
    Stopped,
    Starting,
    Running,
    Failed,
}

/// <summary>
/// mihomo 内核 sidecar 管理（源：CoreService.swift 的 MihomoProcessManager，App 层实现）。
/// 启动参数 -d/-f/-ext-ctl 与工作目录推导对齐 Swift 版；stdout/stderr 重定向经
/// LogFloodGate 防洪后回调 OnLog。终止采用 Process.Kill(entireProcessTree)（Windows 无
/// SIGTERM 软停，对用户态 MVP 无副作用），另以 Job Object 兜底宿主崩溃时的孤儿内核。
/// </summary>
public sealed class MihomoProcessManager : IDisposable
{
    /// <summary>温和停止的退出等待预算（对齐 Swift stop 的 2s+1s 预算语义）。</summary>
    private const int StopTimeoutMilliseconds = 3000;

    /// <summary>退出路径的紧预算（对齐 Swift stopImmediately 的 0.4s+0.2s）。</summary>
    private const int ImmediateStopTimeoutMilliseconds = 600;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);   // 对齐 lifecycleQueue 的串行化
    private readonly JobObjectGuard _jobGuard = new();
    private readonly LogFloodGate _logGate = new();
    private readonly string _binaryPath;

    private Process? _process;
    private CoreLifecycleStatus _status = CoreLifecycleStatus.Stopped;
    private int? _runningPid;
    private bool _intentionalStop;

    /// <summary>内核日志行（已防洪去重）。回调可能来自线程池线程。</summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>内核意外终止（非主动 stop）时的回调，参数为退出码。</summary>
    public Action<int>? OnTermination { get; set; }

    public CoreLifecycleStatus Status
    {
        get { lock (_lock) return _status; }
    }

    public int? RunningProcessId
    {
        get { lock (_lock) return _runningPid; }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>当前会探测的内核二进制路径。</summary>
    public string DetectedBinaryPath => _binaryPath;

    /// <summary>内核二进制是否已放置（no-core 策略：缺失时由界面引导用户放入 core 目录，不尝试启动）。</summary>
    public bool IsBinaryPresent => File.Exists(_binaryPath);

    /// <summary>
    /// 构造管理器。默认在 %APPDATA%\ClashBarX\core\mihomo.exe 探测内核（no-core 策略）；
    /// 测试可注入假内核路径。
    /// </summary>
    public MihomoProcessManager(string? binaryPath = null)
    {
        _binaryPath = binaryPath ?? ClashBarXPaths.DefaultBinaryPath();
    }

    /// <summary>
    /// 启动内核。已在运行时幂等返回当前状态；二进制缺失抛
    /// <see cref="MihomoBinaryResolutionException"/>。
    /// </summary>
    public async Task<CoreLifecycleStatus> StartAsync(
        string configPath,
        string? workingDirectory = null,
        string controller = "127.0.0.1:9090",
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return StartCore(configPath, workingDirectory, controller);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>温和停止：杀进程树并等待退出（经操作串行队列）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopCore(StopTimeoutMilliseconds);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// 退出路径的立即停止：不经操作队列、紧预算，绝不让退出流程悬挂
    /// （源：stopImmediately 的主线程调用约束）。
    /// </summary>
    public void StopImmediately() => StopCore(ImmediateStopTimeoutMilliseconds);

    public async Task<CoreLifecycleStatus> RestartAsync(
        string configPath,
        string? workingDirectory = null,
        string controller = "127.0.0.1:9090",
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopCore(StopTimeoutMilliseconds);
            return StartCore(configPath, workingDirectory, controller);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            StopCore(ImmediateStopTimeoutMilliseconds);
        }
        finally
        {
            // 关 Job 句柄 = kill-on-close 兜底：宿主退出后内核（含孙进程）被系统终结
            _jobGuard.Dispose();
            _operationGate.Dispose();
        }
    }

    // ---- 内部实现 ----

    private CoreLifecycleStatus StartCore(string configPath, string? workingDirectory, string controller)
    {
        lock (_lock)
        {
            if (_process is { HasExited: false })
            {
                return _status;   // 幂等（源：start 的已运行分支）
            }

            _intentionalStop = false;
            _status = CoreLifecycleStatus.Starting;
        }

        if (!File.Exists(_binaryPath))
        {
            var reason = new MihomoBinaryResolutionException(Path.GetDirectoryName(_binaryPath) ?? _binaryPath);
            lock (_lock)
            {
                _status = CoreLifecycleStatus.Failed;
            }

            OnLog?.Invoke($"[mihomo error] {reason.Message}");
            throw reason;
        }

        string resolvedWorkingDirectory = workingDirectory ?? ResolveWorkingDirectory(configPath);
        var startInfo = new ProcessStartInfo(_binaryPath)
        {
            WorkingDirectory = resolvedWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // 源：args = ["-d", workdir, "-f", configPath, "-ext-ctl", controller]
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(resolvedWorkingDirectory);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("-ext-ctl");
        startInfo.ArgumentList.Add(controller);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => HandleOutputLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => HandleOutputLine(eventArgs.Data);
        process.Exited += (_, _) => HandleProcessTermination(process);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Job Object 兜底失败不阻断启动（Kill(entireProcessTree) 仍在主动退出路径生效）
            if (!_jobGuard.TryAssign(process))
            {
                OnLog?.Invoke("[mihomo job] failed to assign job object; crash-orphan guard inactive");
            }
        }
        catch (Exception error)
        {
            process.Dispose();
            string reason = $"Failed to launch mihomo: {error.Message}";
            lock (_lock)
            {
                _status = CoreLifecycleStatus.Failed;
                _intentionalStop = false;
            }

            OnLog?.Invoke($"[mihomo error] {reason}");
            throw;
        }

        lock (_lock)
        {
            _process = process;
            _runningPid = process.Id;
            _status = CoreLifecycleStatus.Running;
        }

        OnLog?.Invoke(
            $"[mihomo started] pid={process.Id} controller={controller} binary={_binaryPath} workdir={resolvedWorkingDirectory}");
        return CoreLifecycleStatus.Running;
    }

    private void StopCore(int timeoutMilliseconds)
    {
        Process? process;
        lock (_lock)
        {
            _intentionalStop = true;
            process = _process;
        }

        if (process == null)
        {
            lock (_lock)
            {
                _status = CoreLifecycleStatus.Stopped;
                _intentionalStop = false;
            }

            return;
        }

        if (process.HasExited)
        {
            HandleProcessTermination(process);
            return;
        }

        // Windows 无 SIGTERM 软停：直接杀整棵进程树（= SIGKILL 语义）；
        // TUN 阶段上服务模式时再考虑优雅停机
        OnLog?.Invoke($"[mihomo stop] kill process tree pid={process.Id}");
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程恰在此时退出
        }
        catch (Win32Exception)
        {
            // 进程已死或权限竞态，继续走终态确认
        }

        try
        {
            process.WaitForExit(timeoutMilliseconds);
        }
        catch (SystemException)
        {
            // 等待失败不阻塞退出路径
        }

        HandleProcessTermination(process);
    }

    /// <summary>区分有意停止与意外终止，清理状态并回调（源：handleProcessTermination）。</summary>
    private void HandleProcessTermination(Process terminated)
    {
        bool handled;
        bool intentional;
        lock (_lock)
        {
            if (!ReferenceEquals(_process, terminated))
            {
                return;
            }

            intentional = _intentionalStop;
            _intentionalStop = false;
            _process = null;
            _runningPid = null;
            _status = CoreLifecycleStatus.Stopped;
            handled = true;
        }

        if (!handled)
        {
            return;
        }

        int exitCode = 0;
        try
        {
            exitCode = terminated.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // 被杀进程拿不到退出码时按 0 处理
        }

        if (intentional)
        {
            OnLog?.Invoke($"[mihomo stopped] exit={exitCode}");
        }
        else
        {
            OnLog?.Invoke($"[mihomo terminated] exit={exitCode}");
            OnTermination?.Invoke(exitCode);
        }

        terminated.Dispose();
    }

    private void HandleOutputLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        // 已由 DataReceived 按行切分（对齐 LineAccumulator 的行语义；
        // EOF 无换行尾巴的场景 mihomo 日志不产生，不单独处理）
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        foreach (string accepted in _logGate.Accept(new[] { trimmed }))
        {
            OnLog?.Invoke(accepted);
        }
    }

    /// <summary>
    /// 工作目录推导：配置文件所在目录；目录名为 "config" 时取其父目录
    /// （源：resolveWorkingDirectoryURL；符号链接解析在 Windows 上省略）。
    /// </summary>
    internal static string ResolveWorkingDirectory(string configPath)
    {
        string directory = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        if (string.Equals(Path.GetFileName(directory), "config", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(directory);
            if (parent != null)
            {
                return parent;
            }
        }

        return directory;
    }
}
