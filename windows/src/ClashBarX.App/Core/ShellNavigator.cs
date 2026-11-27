using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ClashBarX.App.Core;

/// <summary>
/// 用系统默认程序打开目录 / 网址（App 层）。失败不抛出——返回 false 并给出原因，由调用方决定如何反馈。
/// </summary>
internal static class ShellNavigator
{
    /// <summary>在资源管理器中打开目录（不存在则先创建）。</summary>
    public static bool TryOpenDirectory(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>用默认浏览器打开网址。</summary>
    public static bool TryOpenUrl(string url, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
