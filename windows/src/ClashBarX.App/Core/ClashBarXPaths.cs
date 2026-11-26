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
