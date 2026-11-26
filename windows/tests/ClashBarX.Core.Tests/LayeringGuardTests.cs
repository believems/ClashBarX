using System.Reflection;
using ClashBarX.Core.Mihomo;

namespace ClashBarX.Core.Tests;

/// <summary>
/// 分层红线守护（源：MIGRATION-GUIDE.md §1 / §8）：Core 层零 Win32 / WPF / P/Invoke 依赖，
/// CI 将在 Linux 上复跑本套件验证可移植性。
/// </summary>
public class LayeringGuardTests
{
    private static Assembly CoreAssembly => typeof(MihomoApiClient).Assembly;

    /// <summary>禁止引用 WPF / WinForms / 注册表 / 服务控制等 Windows 专属程序集。</summary>
    [Fact]
    public void Core程序集不得引用Windows专属程序集()
    {
        string[] forbidden =
        {
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase",
            "System.Windows.Forms",
            "Microsoft.Win32.Registry",
            "System.ServiceProcess",
            "Microsoft.VisualBasic",
            "Accessibility",
        };

        var referenced = CoreAssembly.GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToList();

        var violations = referenced.Where(name => forbidden.Contains(name)).ToList();
        Assert.True(violations.Count == 0, $"Core 引用了禁用程序集：{string.Join(", ", violations)}");
    }

    /// <summary>禁止任何 P/Invoke（DllImport / LibraryImport）。</summary>
    [Fact]
    public void Core程序集不得包含PInvoke()
    {
        var methodsWithPlatformInvoke = CoreAssembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute(typeof(System.Runtime.InteropServices.DllImportAttribute)) != null
                || method.GetCustomAttribute(typeof(System.Runtime.InteropServices.LibraryImportAttribute)) != null)
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToList();

        Assert.True(methodsWithPlatformInvoke.Count == 0,
            $"Core 存在 P/Invoke：{string.Join(", ", methodsWithPlatformInvoke)}");
    }
}
