using System.IO;

namespace ClashBarX.App.Core;

/// <summary>
/// 首次使用引导链（装内核 → 导订阅 → 启动 → 内容出现）的纯判定逻辑；呈现由 App 装配层承担。
/// </summary>
public static class FirstRunGuidePolicy
{
    /// <summary>是否存在可启动的运行配置：选中的配置文件在，或根目录手工 config.yaml 在。</summary>
    public static bool HasRunnableConfig(string? selectedConfigPath, string defaultConfigPath) =>
        (selectedConfigPath is not null && File.Exists(selectedConfigPath)) || File.Exists(defaultConfigPath);

    /// <summary>
    /// 导入成功是否需要对话框反馈（源：app.config.remote_import.alert.success.title）：
    /// 首次导入（此前没有任何配置文件）给引导性提示，其后只记日志不打断；
    /// 内核缺失时不弹成功框——由无内核安装引导接管下一步。
    /// </summary>
    public static bool ShouldAnnounceImportSuccess(int configFileCountBeforeImport, bool coreBinaryPresent) =>
        configFileCountBeforeImport == 0 && coreBinaryPresent;
}
