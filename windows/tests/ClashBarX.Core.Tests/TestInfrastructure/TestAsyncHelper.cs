namespace ClashBarX.Core.Tests.TestInfrastructure;

/// <summary>异步轮询等待辅助：条件满足返回，超时抛异常（避免测试挂死）。</summary>
public static class TestAsyncHelper
{
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        string description,
        int timeoutMilliseconds = 5000,
        int pollMilliseconds = 10)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException($"等待超时：{description}");
            }

            await Task.Delay(pollMilliseconds);
        }
    }
}
