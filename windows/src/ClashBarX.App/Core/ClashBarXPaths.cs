using System.IO;

namespace ClashBarX.App.Core;

/// <summary>运行时数据目录约定（源：MIGRATION-GUIDE.md §5，%APPDATA%\ClashBarX）。</summary>
public static class ClashBarXPaths
{
    /// <summary>数据根目录（配置 / 订阅 / 状态；内核与 Geo 资源在其 core/ 子目录）。</summary>
    public static string AppDataRoot(string? overrideBase = null) =>
        overrideBase ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClashBarX");

    /// <summary>mihomo 内核目录（no-core 策略：首启引导用户放置 mihomo.exe）。</summary>
    public static string CoreDirectory(string? overrideBase = null) =>
        Path.Combine(AppDataRoot(overrideBase), "core");

    /// <summary>默认内核二进制位置。</summary>
    public static string DefaultBinaryPath(string? overrideBase = null) =>
        Path.Combine(CoreDirectory(overrideBase), "mihomo.exe");

    /// <summary>默认运行配置文件位置（M2 订阅导入前的手工配置入口）。</summary>
    public static string DefaultConfigPath(string? overrideBase = null) =>
        Path.Combine(AppDataRoot(overrideBase), "config.yaml");

    /// <summary>规则覆写三文件目录（源：workingDirectoryManager.root/overrides）。</summary>
    public static string OverridesDirectory(string? overrideBase = null) =>
        Path.Combine(AppDataRoot(overrideBase), "overrides");

    /// <summary>覆写生成的运行时配置目录（源：stateDirectory/runtime）。</summary>
    /// <summary>前置代理注入版运行时配置目录（源：macOS stateDirectory/corp）。</summary>
    public static string CorpProxyDirectory(string? overrideBase = null) =>
        Path.Combine(AppDataRoot(overrideBase), "state", "corp");

    public static string RuntimeConfigDirectory(string? overrideBase = null) =>
        Path.Combine(AppDataRoot(overrideBase), "state", "runtime");
}

/// <summary>内核二进制缺失（源：CoreService.swift 的 MihomoBinaryResolutionError）。</summary>
public sealed class MihomoBinaryResolutionException : Exception
{
    public string ExpectedPath { get; }

    public MihomoBinaryResolutionException(string expectedPath)
        : base($"mihomo binary not found. Expected an executable named 'mihomo.exe' in {expectedPath}.")
    {
        ExpectedPath = expectedPath;
    }
}
