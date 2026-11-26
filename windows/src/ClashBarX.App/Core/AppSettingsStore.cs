using System.IO;
using System.Text.Json;

namespace ClashBarX.App.Core;

/// <summary>应用设置（M2 阶段仅系统代理开关的会话间持久化）。</summary>
public sealed record AppSettings(bool SystemProxyEnabled)
{
    public static readonly AppSettings Default = new(false);
}

/// <summary>设置的 JSON 文件存取（%APPDATA%\ClashBarX\settings.json）。</summary>
public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AppSettingsStore(string? filePath = null)
    {
        _path = filePath ?? Path.Combine(ClashBarXPaths.AppDataRoot(), "settings.json");
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return AppSettings.Default;
                }

                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
                return settings ?? AppSettings.Default;
            }
            catch (JsonException)
            {
                return AppSettings.Default;
            }
            catch (IOException)
            {
                return AppSettings.Default;
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
