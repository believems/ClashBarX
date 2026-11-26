using System.Runtime.InteropServices;
using ClashBarX.App.Core;
using Microsoft.Win32;

namespace ClashBarX.App.Tests.Core;

/// <summary>注册表监听真键测试：值变更应触发回调。</summary>
public class RegistryWatcherTests
{
    [Fact]
    public async Task 值变更触发回调()
    {
        string path = $@"Software\ClashBarX-Tests\watcher-{Guid.NewGuid():N}";
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("value", 0);
        }

        try
        {
            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new RegistryWatcher(path, () => fired.TrySetResult(true));
            watcher.Start();
            await Task.Delay(300);   // 等 RegNotify 挂上

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            Assert.NotNull(key);
            key.SetValue("value", 1);

            Task completed = await Task.WhenAny(fired.Task, Task.Delay(5000));
            Assert.True(fired.Task.IsCompleted, "5 秒内未收到变更回调");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task 连续多次变更持续触发()
    {
        string path = $@"Software\ClashBarX-Tests\watcher-{Guid.NewGuid():N}";
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("value", 0);
        }

        try
        {
            int firedCount = 0;
            var gate = new object();
            using var watcher = new RegistryWatcher(path, () => { lock (gate) firedCount++; });
            watcher.Start();
            await Task.Delay(300);

            for (int i = 1; i <= 3; i++)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                key!.SetValue("value", i);
                await Task.Delay(500);
            }

            await TestAsyncHelper2.WaitUntilAsync(() => { lock (gate) return firedCount >= 2; }, "至少两次回调");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}

file static class TestAsyncHelper2
{
    public static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"等待超时：{description}");
            }

            await Task.Delay(20);
        }
    }
}
