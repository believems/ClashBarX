using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClashBarX.App.Core;

/// <summary>
/// 注册表键变更监听（App 层 Win32 隔离）：RegNotifyChangeKeyValue 挂在
/// HKCU Internet Settings 上，值变更时回调（由 SystemProxyService 去抖判定守护回写）。
/// </summary>
public sealed partial class RegistryWatcher : IDisposable
{
    private const int RegNotifyChangeLastSet = 0x4;

    private readonly string _registryPath;
    private readonly Action _onChange;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loopTask;
    private volatile bool _started;

    /// <summary>诊断日志（挂载/触发/失败分支），回调来自后台线程。</summary>
    public Action<string>? Logged { get; set; }

    public RegistryWatcher(string registryPath, Action onChange)
    {
        _registryPath = registryPath;
        _onChange = onChange;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _loopTask = Task.Run(() => WatchLoopAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        if (!_started)
        {
            _cancellation.Dispose();
            return;
        }

        _cancellation.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 循环内的等待异常不阻塞销毁
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task WatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WatchOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 注册表键暂时不可用等情况：退避后重试
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchOnceAsync(CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            _registryPath,
            RegistryKeyPermissionCheck.ReadSubTree,
            System.Security.AccessControl.RegistryRights.Notify);
        if (key == null)
        {
            Logged?.Invoke($"[proxy-watch] open failed: {_registryPath}");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return;
        }

        using var signal = new ManualResetEvent(false);
        // 返回 LSTATUS（0 = ERROR_SUCCESS），不是 BOOL
        int status = RegNotifyChangeKeyValue(
            key.Handle,
            watchSubtree: false,
            RegNotifyChangeLastSet,
            signal.SafeWaitHandle,
            asynchronous: true);
        if (status != 0)
        {
            Logged?.Invoke($"[proxy-watch] RegNotify status={status}");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return;
        }

        Logged?.Invoke("[proxy-watch] armed");

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            signal,
            (_, _) => completion.TrySetResult(true),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
        await using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        await completion.Task.ConfigureAwait(false);
        registration.Unregister(null);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Logged?.Invoke("[proxy-watch] fired");
        _onChange();
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int RegNotifyChangeKeyValue(
        Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int dwNotifyFilter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
