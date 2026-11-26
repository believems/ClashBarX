using System.Windows;
using Microsoft.Win32;

namespace ClashBarX.App.Core;

/// <summary>
/// 外观与语言应用（源：applyAppAppearance / setUILanguage）：
/// 以标记字典热替换实现 DynamicResource 即时生效（无需重启）。
/// </summary>
public static class AppearanceManager
{
    private static readonly Uri LightTheme = MakeUri("/Themes/Light.xaml");
    private static readonly Uri DarkTheme = MakeUri("/Themes/Dark.xaml");
    private static readonly Uri ChineseStrings = MakeUri("/Strings/zh-Hans.xaml");
    private static readonly Uri EnglishStrings = MakeUri("/Strings/en.xaml");

    public static void ApplyTheme(AppAppearanceMode mode)
    {
        Uri uri = ResolveTheme(mode);
        ReplaceDictionary("ClashBarX.Theme", uri);
    }

    public static void ApplyLanguage(AppLanguage language)
    {
        ReplaceDictionary(
            "ClashBarX.Strings",
            language == AppLanguage.English ? EnglishStrings : ChineseStrings);
        // 代码侧动态文案同步切换（CoreL10n 为 VM/服务产出的文案提供字典）
        ClashBarX.Core.L10n.CoreL10n.Language = language == AppLanguage.English
            ? ClashBarX.Core.L10n.CoreLanguage.English
            : ClashBarX.Core.L10n.CoreLanguage.ChineseSimplified;
    }

    /// <summary>系统是否偏好深色（源：NSAppAppearance 跟随系统；读 AppsUseLightTheme）。</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;   // 读取失败按浅色
        }
    }

    private static Uri ResolveTheme(AppAppearanceMode mode) => mode switch
    {
        AppAppearanceMode.Dark => DarkTheme,
        AppAppearanceMode.Light => LightTheme,
        _ => SystemPrefersDark() ? DarkTheme : LightTheme,
    };

    /// <summary>替换带标记的合并字典（标记字典的键保持资源查找稳定）。</summary>
    private static void ReplaceDictionary(string markerKey, Uri source)
    {
        ResourceDictionary merged = Application.Current.Resources;
        for (int index = 0; index < merged.MergedDictionaries.Count; index++)
        {
            if (merged.MergedDictionaries[index].Contains(markerKey))
            {
                var replaced = new ResourceDictionary { Source = source };
                replaced[markerKey] = true;
                merged.MergedDictionaries[index] = replaced;
                return;
            }
        }

        var dictionary = new ResourceDictionary { Source = source };
        dictionary[markerKey] = true;
        merged.MergedDictionaries.Add(dictionary);
    }

    private static Uri MakeUri(string relative) => new(relative, UriKind.Relative);
}
