using System.Runtime.InteropServices;
using ClashBarX.Core.SystemProxy;
using Microsoft.Win32;

namespace ClashBarX.App.Core;

/// <summary>
/// 系统代理的注册表实现（迁移指南 §5）：HKCU\...\Internet Settings 的
/// ProxyEnable / ProxyServer / ProxyOverride，写后以 InternetSetOption
/// 广播设置变更与刷新。registryPath/broadcast 可注入供测试使用隔离子键。
/// </summary>
public sealed partial class WindowsSystemProxyController : ISystemProxyController
{
    /// <summary>系统代理注册表键（HKCU 下相对路径）。</summary>
    public const string InternetSettingsRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private readonly string _registryPath;
    private readonly bool _broadcast;

    public WindowsSystemProxyController(string? registryPath = null, bool broadcast = true)
    {
        _registryPath = registryPath ?? InternetSettingsRegistryPath;
        _broadcast = broadcast;
    }

    public SystemProxySnapshot Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath);
        if (key == null)
        {
            return SystemProxySnapshot.Disabled;
        }

        bool enabled = key.GetValue("ProxyEnable") is int value && value != 0;
        string? server = key.GetValue("ProxyServer") as string;
        string? proxyOverride = key.GetValue("ProxyOverride") as string;
        return new SystemProxySnapshot(enabled, server, SystemProxyBypass.Parse(proxyOverride));
    }

    public void Enable(SystemProxyTarget target)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true);
        // 先写服务器与绕过列表，最后开 ProxyEnable：开启瞬间读到的值保持一致
        key.SetValue("ProxyServer", target.ServerDisplay, RegistryValueKind.String);
        key.SetValue("ProxyOverride", SystemProxyDefaults.RenderOverride(target.BypassList), RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        BroadcastChange();
    }

    public void Disable()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true);
        // 仅翻开关，保留 Server/Override 便于再次启用
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        BroadcastChange();
    }

    private void BroadcastChange()
    {
        if (!_broadcast)
        {
            return;
        }

        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [LibraryImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
