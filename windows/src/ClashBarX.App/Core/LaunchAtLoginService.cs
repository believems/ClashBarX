using Microsoft.Win32;

namespace ClashBarX.App.Core;

/// <summary>
/// 开机自启（源：LaunchAtLoginService.swift 的 SMAppService；Windows 对应物为
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表键——无需管理员权限、
/// 无需审批弹窗，与 SMAppService.register 的用户体验最接近）。
/// 键名可注入（测试用随机名避免污染真实键）。
/// </summary>
public sealed class LaunchAtLoginService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public LaunchAtLoginService(string valueName = "ClashBarX") => _valueName = valueName;

    /// <summary>键名（测试与诊断用）。</summary>
    public string RegistryLocation => $@"HKEY_CURRENT_USER\{RunKeyPath}\{_valueName}";

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_valueName) is string value && value.Length > 0;
    }

    /// <summary>写入/移除自启项。exePath 为当前程序完整路径。</summary>
    public void SetEnabled(bool enabled, string exePath)
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("无法打开 HKCU Run 注册表键");
        }

        if (enabled)
        {
            // 引号包裹防路径含空格
            key.SetValue(_valueName, $"\"{exePath}\"");
        }
        else if (key.GetValue(_valueName) is not null)
        {
            key.DeleteValue(_valueName);
        }
    }

    /// <summary>开关并复核（源：setEnabled 后复查 status 的语义）。</summary>
    public void Apply(bool enabled, string exePath)
    {
        SetEnabled(enabled, exePath);
        if (IsEnabled() != enabled)
        {
            throw new InvalidOperationException(enabled ? "自启写入后未生效" : "自启移除后仍未清除");
        }
    }
}
