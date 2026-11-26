using System.Diagnostics;
using System.Security.Principal;

namespace ClashBarX.App.Core;

/// <summary>
/// Windows 提权判定与 UAC 重启（macOS 侧对应物：TunService 的 AdministratorShell setuid 路径）。
/// Windows 无 setuid 语义——mihomo 开 TUN 需要内核进程持有管理员令牌，而内核由本应用
/// 拉起，因此权限语义收敛为「应用以管理员身份运行」（子进程继承令牌）。
/// </summary>
public static class WindowsElevationService
{
    /// <summary>当前进程是否已提权（管理员令牌；UAC 关闭时管理员组直通亦为 true）。</summary>
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 以管理员身份重启应用（runas verb 提权启动新实例）。返回是否成功启动；
    /// 用户在 UAC 弹窗点「否」返回 false（调用方回滚开关意图并如实反馈）。
    /// </summary>
    public static bool TryRestartAsAdministrator(out string? failureMessage)
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            failureMessage = "无法确定应用可执行文件路径";
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
            });
            failureMessage = null;
            return true;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            // 用户取消了 UAC 授权（源：authorizationCancelled 的对应物）
            failureMessage = "已取消管理员授权";
            return false;
        }
        catch (Exception error)
        {
            failureMessage = error.Message;
            return false;
        }
    }
}
